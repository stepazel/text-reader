using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Win32.SafeHandles;

namespace TextReader;

public partial class MainWindow : Window
{
    private const int WindowSize = 500;
    private const int ScrollBuffer = 100;

    private long[] _offsets = [];
    private long _fileLength;
    private SafeFileHandle? _fileHandle;
    private string? _tempFile;
    private long _firstLoadedLine;
    private bool _adjustingScroll;
    private bool _suppressVirtualScroll;
    private bool _isAnimating;
    private int _slideVersion;
    private CancellationTokenSource? _debounceToken;
    private CancellationTokenSource? _scrollAnimToken;

    public AvaloniaList<string> Lines { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Scroller.Focus();
        Closed += (_, _) =>
        {
            _fileHandle?.Dispose();
            if (_tempFile == null) return;
            try { File.Delete(_tempFile); } catch { /* ignore */ }
        };
        DataContext = this;
    }

    private void OpenFile(string path, bool isTempFile = false)
    {
        _fileHandle?.Dispose();
        if (_tempFile != null) { try { File.Delete(_tempFile); } catch { /* ignore */ } }
        _tempFile = isTempFile ? path : null;

        _offsets = BuildLineOffsets(path);
        _fileHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.RandomAccess);
        _fileLength = RandomAccess.GetLength(_fileHandle);
        _firstLoadedLine = 0;

        LoadWindow(0);
        Scroller.Offset = Vector.Zero;

        Dispatcher.UIThread.Post(() =>
        {
            if (Lines.Count == 0) return;
            var lineHeight = Scroller.Extent.Height / Lines.Count;
            var pageLines = (int)(Scroller.Viewport.Height / lineHeight);
            VirtualScroll.Maximum = Math.Max(0, _offsets.Length - pageLines);
            VirtualScroll.SmallChange = 1;
            VirtualScroll.LargeChange = pageLines;
            VirtualScroll.ViewportSize = pageLines;
            VirtualScroll.Value = 0;
        }, DispatcherPriority.Loaded);
    }

    private async void OnOpenFileClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Otevřít textový soubor",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Textové soubory") { Patterns = ["*.txt"] }]
        });

        if (files.Count == 0) return;
        var localPath = files[0].TryGetLocalPath();
        if (localPath == null) return;

        OpenFile(localPath);
    }

    private async void OnOpenUrlClicked(object? sender, RoutedEventArgs e)
    {
        var url = await ShowUrlInputDialog();
        if (string.IsNullOrWhiteSpace(url)) return;

        var statusLabel = new TextBlock
        {
            Text = "Stahování...",
            Margin = new Thickness(20, 20, 20, 8),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var closeBtn = new Button
        {
            Content = "Zavřít",
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var statusWindow = new Window
        {
            Title = "TextReader",
            Width = 300,
            Height = 110,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Children = { statusLabel, closeBtn } }
        };
        closeBtn.Click += (_, _) => statusWindow.Close();
        statusWindow.Show(this);

        try
        {
            using var http = new HttpClient();
            var response = await http.GetAsync(url);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                statusLabel.Text = "404 – Soubor nebyl nalezen";
                closeBtn.IsVisible = true;
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                statusLabel.Text = "Vyskytla se chyba při stahování";
                closeBtn.IsVisible = true;
                return;
            }

            var content = await response.Content.ReadAsStringAsync();
            var tempPath = Path.GetTempFileName();
            await File.WriteAllTextAsync(tempPath, content);
            statusWindow.Close();
            OpenFile(tempPath, isTempFile: true);
        }
        catch
        {
            statusLabel.Text = "Vyskytla se chyba při stahování";
            closeBtn.IsVisible = true;
        }
    }

    private async Task<string?> ShowUrlInputDialog()
    {
        var textBox = new TextBox
        {
            PlaceholderText = "https://",
            Margin = new Thickness(12, 12, 12, 8),
            MinWidth = 360
        };

        var okBtn = new Button { Content = "Otevřít" };
        var cancelBtn = new Button { Content = "Zrušit" };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12),
            Spacing = 8,
            Children = { okBtn, cancelBtn }
        };

        var dialog = new Window
        {
            Title = "Otevřít z URL",
            Width = 420,
            Height = 120,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Children = { textBox, buttons } }
        };

        string? result = null;
        okBtn.Click += (_, _) => { result = textBox.Text; dialog.Close(); };
        cancelBtn.Click += (_, _) => dialog.Close();
        textBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { result = textBox.Text; dialog.Close(); }
            else if (ke.Key == Key.Escape) dialog.Close();
        };

        await dialog.ShowDialog(this);
        return result;
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
            _suppressVirtualScroll = true;
            VirtualScroll.Value = docLine;
            _suppressVirtualScroll = false;
        }, DispatcherPriority.Loaded);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Lines.Count == 0 || Scroller.Extent.Height == 0) { base.OnKeyDown(e); return; }

        var lineHeight = Scroller.Extent.Height / Lines.Count;

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
                AnimatedScrollBy(+Scroller.Viewport.Height);
                e.Handled = true;
                break;
            case Key.PageUp:
                AnimatedScrollBy(-Scroller.Viewport.Height);
                e.Handled = true;
                break;
            case Key.Home:
                NavigateTo(0);
                e.Handled = true;
                break;
            case Key.End:
                NavigateTo(Math.Max(0, _offsets.Length - (int)(Scroller.Viewport.Height / lineHeight)));
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
            Lines.Add(ReadLine((int)(firstLine + i)));
    }

    private async void AnimatedScrollBy(double deltaY)
    {
        _scrollAnimToken?.Cancel();
        _scrollAnimToken = new CancellationTokenSource();
        var token = _scrollAnimToken.Token;

        // Invalidate any pending slide-compensation Post callbacks and reset state
        _slideVersion++;
        _adjustingScroll = false;

        var startY = Scroller.Offset.Y;
        var maxY = Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);
        var targetY = Math.Clamp(startY + deltaY, 0, maxY);

        const int durationMs = 180;
        var startTick = Environment.TickCount64;
        _isAnimating = true;

        try
        {
            while (true)
            {
                var t = Math.Min(1.0, (double)(Environment.TickCount64 - startTick) / durationMs);
                var eased = 1 - Math.Pow(1 - t, 3); // cubic ease-out
                Scroller.Offset = Scroller.Offset.WithY(startY + (targetY - startY) * eased);

                if (t >= 1.0) break;
                await Task.Delay(8, token); // ~120 fps
            }
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            // Only clear flag if this animation wasn't replaced by a newer one
            if (!token.IsCancellationRequested)
                _isAnimating = false;
        }

        ProcessScrollState(Scroller);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || _adjustingScroll || _isAnimating) return;
        if (sender is not ScrollViewer sv) return;
        if (Lines.Count == 0) return;

        ProcessScrollState(sv);
    }

    private void ProcessScrollState(ScrollViewer sv)
    {
        if (Lines.Count == 0 || sv.Extent.Height == 0) return;

        var lineHeight = sv.Extent.Height / Lines.Count;
        var firstVisible = _firstLoadedLine + (int)(sv.Offset.Y / lineHeight);
        var lastVisible = firstVisible + (int)(sv.Viewport.Height / lineHeight);
        var lastLoaded = _firstLoadedLine + Lines.Count - 1;

        _suppressVirtualScroll = true;
        VirtualScroll.Value = firstVisible;
        _suppressVirtualScroll = false;

        if (lastLoaded - lastVisible <= ScrollBuffer)
        {
            if (lastLoaded >= _offsets.Length - 1) return;

            const int changeSize = 100;
            var linesToAdd = new string[changeSize];
            for (var i = 0; i < changeSize; i++)
                linesToAdd[i] = ReadLine((int)(lastLoaded + 1 + i));

            _adjustingScroll = true;
            Lines.AddRange(linesToAdd);
            _firstLoadedLine += changeSize;
            if (Lines.Count > WindowSize)
                Lines.RemoveRange(0, changeSize);

            var downVer = ++_slideVersion;
            Dispatcher.UIThread.Post(() =>
            {
                if (_slideVersion != downVer) return;
                sv.Offset = sv.Offset.WithY(sv.Offset.Y - changeSize * lineHeight);
                _adjustingScroll = false;
            }, DispatcherPriority.Loaded);
            return;
        }

        var bufferRemainingTop = firstVisible - _firstLoadedLine;
        if (bufferRemainingTop <= ScrollBuffer && _firstLoadedLine > 0)
        {
            const int changeSize = 100;
            var linesToAdd = new string[changeSize];
            for (var i = 0; i < changeSize; i++)
                linesToAdd[i] = ReadLine((int)(_firstLoadedLine - changeSize + i));

            _adjustingScroll = true;
            Lines.InsertRange(0, linesToAdd);
            Lines.RemoveRange(Lines.Count - changeSize, changeSize);
            _firstLoadedLine -= changeSize;

            var upVer = ++_slideVersion;
            Dispatcher.UIThread.Post(() =>
            {
                if (_slideVersion != upVer) return;
                sv.Offset = sv.Offset.WithY(sv.Offset.Y + changeSize * lineHeight);
                _adjustingScroll = false;
            }, DispatcherPriority.Loaded);
        }
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
        if (lineIndex < 0 || lineIndex >= _offsets.Length || _fileHandle is null)
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
