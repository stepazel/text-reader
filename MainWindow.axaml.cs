using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Win32.SafeHandles;

namespace TextReader;

public partial class MainWindow : Window
{
    private const int WindowSize = 500;
    private const int ScrollBuffer = 100;

    private readonly long[] _offsets;
    private readonly long _fileLength;
    private readonly SafeFileHandle? _fileHandle;
    private long _firstLoadedLine;
    private bool _adjustingScroll;
    private bool _suppressVirtualScroll;
    private CancellationTokenSource? _debounceToken;

    public AvaloniaList<string> Lines { get; } = [];

    public MainWindow()
    {
        InitializeComponent();

        var path = Path.Combine("Test", "big.txt");
        _offsets = BuildLineOffsets(path);

        _fileHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.RandomAccess);
        _fileLength = RandomAccess.GetLength(_fileHandle);

        LoadWindow(0);
        Opened += (_, _) =>
        {
            Scroller.Focus(); // Zajistí handlování klávesových eventů
            var lineHeight = Scroller.Extent.Height / Lines.Count;
            VirtualScroll.Maximum = _offsets.Length - 1;
            VirtualScroll.ViewportSize = (int)(Scroller.Viewport.Height / lineHeight);
        };
        Closed += (_, _) => _fileHandle.Dispose();
        DataContext = this;
    }

    private void NavigateTo(long docLine)
    {
        if (Lines.Count == 0 || Scroller.Extent.Height == 0) return;

        var lineHeight = Scroller.Extent.Height / Lines.Count;
        docLine = Math.Clamp(docLine, 0, _offsets.Length - 1);
        LoadWindow(Math.Max(0, docLine - WindowSize / 2));

        var lineInBuffer = docLine - _firstLoadedLine;
        _adjustingScroll = true;
        Dispatcher.UIThread.Post(() =>
        {
            Scroller.Offset = new Vector(0, lineInBuffer * lineHeight);
            _adjustingScroll = false;
        }, DispatcherPriority.Loaded);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Lines.Count == 0 || Scroller.Extent.Height == 0) { base.OnKeyDown(e); return; }

        var lineHeight = Scroller.Extent.Height / Lines.Count;

        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
        switch (e.Key)
        {
            case Key.Down:
                Scroller.Offset = Scroller.Offset.WithY(Scroller.Offset.Y + lineHeight);
                e.Handled = true;
                break;
            case Key.Up:
                Scroller.Offset = Scroller.Offset.WithY(Scroller.Offset.Y - lineHeight);
                e.Handled = true;
                break;
            case Key.PageDown:
                Scroller.Offset = Scroller.Offset.WithY(Scroller.Offset.Y + Scroller.Viewport.Height);
                e.Handled = true;
                break;
            case Key.PageUp:
                Scroller.Offset = Scroller.Offset.WithY(Scroller.Offset.Y - Scroller.Viewport.Height);
                e.Handled = true;
                break;
            case Key.Home:
                NavigateTo(0);
                e.Handled = true;
                break;
            case Key.End:
                NavigateTo(_offsets.Length - 1);
                e.Handled = true;
                break;
            default:
                base.OnKeyDown(e);
                break;
        }
    }

    private void LoadWindow(long firstLine)
    {
        firstLine = Math.Clamp(firstLine, 0, Math.Max(0, _offsets.Length - WindowSize));
        _firstLoadedLine = firstLine;

        Lines.Clear();
        var count = (int)Math.Min(WindowSize, _offsets.Length - firstLine);
        for (var i = 0; i < count; i++)
        {
            Lines.Add(ReadLine((int)(firstLine + i)));
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || _adjustingScroll)
        {
            return;
        }

        if (sender is not ScrollViewer sv)
        {
            return;
        }

        var lineHeight = sv.Extent.Height / Lines.Count;
        var firstVisible = _firstLoadedLine + (int)(sv.Offset.Y / lineHeight);
        var lastVisible = firstVisible + (int)(sv.Viewport.Height / lineHeight);
        var lastLoaded = _firstLoadedLine + Lines.Count - 1;

        if (lastLoaded - lastVisible <= ScrollBuffer) // Slide down
        {
            if (lastLoaded >= _offsets.Length - 1) return;

            const int changeSize = 100;
            var linesToAdd = new string[changeSize];
            for (var i = 0; i < changeSize; i++)
            {
                linesToAdd[i] = ReadLine((int)(lastLoaded + 1 + i));
            }

            _adjustingScroll = true;
            Lines.AddRange(linesToAdd);
            _firstLoadedLine += changeSize;
            if (Lines.Count > WindowSize)
            {
                Lines.RemoveRange(0, changeSize);
            }

            Dispatcher.UIThread.Post(() =>
            {
                sv.Offset = sv.Offset.WithY(sv.Offset.Y - changeSize * lineHeight);
                _adjustingScroll = false;
            }, DispatcherPriority.Loaded);
            return;
        }
        

        var bufferRemainingTop = firstVisible - _firstLoadedLine; // kolik řádků je nad prvním viditelným 
        if (bufferRemainingTop <= ScrollBuffer && _firstLoadedLine > 0) // Slide up
        {
            const int changeSize = 100;

            var linesToAdd = new string[changeSize];
            for (var i = 0; i < changeSize; i++)
            {
                linesToAdd[i] = ReadLine((int)(_firstLoadedLine - changeSize + i));
            }

            _adjustingScroll = true;
            Lines.InsertRange(0, linesToAdd);
            Lines.RemoveRange(Lines.Count - changeSize, changeSize);
            _firstLoadedLine -= changeSize;

            Dispatcher.UIThread.Post(() =>
            {
                sv.Offset = sv.Offset.WithY(sv.Offset.Y + changeSize * lineHeight);
                _adjustingScroll = false;
            }, DispatcherPriority.Loaded);
        }


        _suppressVirtualScroll = true;
        VirtualScroll.Value = firstVisible;
        _suppressVirtualScroll = false;
    }

    private async void OnVirtualScrollChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVirtualScroll) return;

        _debounceToken?.Cancel();
        _debounceToken = new CancellationTokenSource();
        try
        {
            await Task.Delay(80, _debounceToken.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        NavigateTo((long)e.NewValue);
    }


    private string ReadLine(int lineIndex)
    {
        if (lineIndex >= _offsets.Length || _fileHandle is null)
            return string.Empty;

        var start = _offsets[lineIndex];
        var end = lineIndex + 1 < _offsets.Length ? _offsets[lineIndex + 1] : _fileLength;

        var bytes = new byte[end - start];
        RandomAccess.Read(_fileHandle, bytes, start);

        return Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
    }

    private static long[] BuildLineOffsets(string path)
    {
        var offsets = new List<long> { 0L };

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536);
        var buffer = new byte[65536];
        long position = 0;
        int bytesRead;

        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == (byte)'\n')
                    offsets.Add(position + i + 1);
            }

            position += bytesRead;
        }

        return offsets.ToArray();
    }
}