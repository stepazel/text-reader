using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;

namespace TextReader;

public partial class MainWindow : Window
{
    private const int WindowSize = 300;
    private const int SlideAmount = 50;

    private long[] _offsets = [];
    private string _path = "";
    private long _firstLoadedLine = 0;
    private bool _adjustingScroll;

    public ObservableCollection<string> Lines { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        _path = Path.Combine("Test", "big.txt");
        _offsets = BuildLineOffsets(_path);

        var initialCount = (int)Math.Min(WindowSize, _offsets.Length);
        for (var i = 0; i < initialCount; i++)
            Lines.Add(ReadLine(_path, _offsets, i));

        DataContext = this;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // ExtentDelta != 0 means our own content change fired this event, not user scrolling
        if (e.ExtentDelta.Y != 0 || _adjustingScroll)
            return;

        if (sender is not ScrollViewer sv) return;

        var offset = sv.Offset.Y;
        var viewport = sv.Viewport.Height;
        var distanceFromBottom = sv.Extent.Height - viewport - offset;

        if (distanceFromBottom < viewport / 2)
            SlideDown();
        else if (offset < viewport / 2)
            SlideUp(sv);
    }

    private void SlideDown()
    {
        var nextLine = _firstLoadedLine + Lines.Count;
        if (nextLine >= _offsets.Length) return;

        var toAdd = (int)Math.Min(SlideAmount, _offsets.Length - nextLine);
        for (var i = 0; i < toAdd; i++)
            Lines.Add(ReadLine(_path, _offsets, (int)(nextLine + i)));

        // Trim from top to keep window bounded
        while (Lines.Count > WindowSize)
        {
            Lines.RemoveAt(0);
            _firstLoadedLine++;
        }
    }

    private void SlideUp(ScrollViewer sv)
    {
        if (_firstLoadedLine == 0) return;

        var toAdd = (int)Math.Min(SlideAmount, _firstLoadedLine);
        var oldExtent = sv.Extent.Height;

        for (var i = toAdd - 1; i >= 0; i--)
            Lines.Insert(0, ReadLine(_path, _offsets, (int)(_firstLoadedLine - toAdd + i)));

        _firstLoadedLine -= toAdd;

        // Trim from bottom to keep window bounded
        while (Lines.Count > WindowSize)
            Lines.RemoveAt(Lines.Count - 1);

        // Shift scroll offset to compensate for the height added above the viewport
        _adjustingScroll = true;
        Dispatcher.UIThread.Post(() =>
        {
            sv.Offset = sv.Offset.WithY(sv.Offset.Y + (sv.Extent.Height - oldExtent));
            _adjustingScroll = false;
        }, DispatcherPriority.Loaded);
    }

    private static long[] BuildLineOffsets(string path)
    {
        var offsets = new List<long> { 0L };

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
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

    private static string ReadLine(string path, long[] offsets, int lineIndex)
    {
        if (lineIndex >= offsets.Length)
            return string.Empty;

        var start = offsets[lineIndex];
        var end = lineIndex + 1 < offsets.Length
            ? offsets[lineIndex + 1]
            : new FileInfo(path).Length;

        var bytes = new byte[end - start];
        using var handle = File.OpenHandle(path);
        RandomAccess.Read(handle, bytes, start);

        return Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
    }
}