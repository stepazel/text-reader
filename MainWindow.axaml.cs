using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia.Controls;

namespace TextReader;

public partial class MainWindow : Window
{
    public List<string> Lines { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        // Assumes working directory is project root (default in Rider)
        var path = Path.Combine("Test", "easy.txt");
        var offsets = BuildLineOffsets(path);

        for (var i = 10; i <= 30; i++)
            Lines.Add(ReadLine(path, offsets, i));

        DataContext = this;
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

    // Reads a single line by seeking directly to its byte offset.
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