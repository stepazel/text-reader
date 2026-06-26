using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private string? _filePath;
    private long _firstLoadedLine;
    private bool _adjustingScroll;
    private bool _suppressVirtualScroll;
    private CancellationTokenSource? _debounceToken;

    private string _activeQuery = "";
    private long[] _searchResults = [];
    private int _currentResultIndex = -1;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _searchDebounceToken;
    private int _searchGeneration;
    private LineItem? _lastHighlightedItem;

    public AvaloniaList<LineItem> Lines { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Scroller.Focus();
        this.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.F)
            {
                return;
            }

            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                return;
            }

            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            _fileHandle?.Dispose();
            _searchCts?.Cancel();
            if (_tempFile == null)
            {
                return;
            }

            try
            {
                File.Delete(_tempFile);
            }
            catch
            {
                /* ignore */
            }
        };
        DataContext = this;
    }

    private async Task OpenFile(string path, bool isTempFile = false)
    {
        _searchCts?.Cancel();
        _searchResults = [];
        _currentResultIndex = -1;
        SearchStatus.Text = "";
        SearchProgress.IsVisible = false;

        _fileHandle?.Dispose();
        if (_tempFile != null)
        {
            try
            {
                File.Delete(_tempFile);
            }
            catch
            {
                /* ignore */
            }
        }

        _tempFile = isTempFile ? path : null;

        var progressBar = new ProgressBar
            { Minimum = 0, Maximum = 100, Value = 0, Width = 260, Margin = new Thickness(16, 16, 16, 8) };
        var progressLabel = new TextBlock
            { Text = "0 %", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
        var progressWindow = new Window
        {
            Title = "Načítání souboru...",
            Width = 300,
            Height = 100,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Children = { progressBar, progressLabel } },
        };
        progressWindow.Show(this);

        var progress = new Progress<double>(pct =>
        {
            progressBar.Value = pct;
            progressLabel.Text = $"{pct:0} %";
        });

        var stopwatch = Stopwatch.StartNew();
        _offsets = await Task.Run(() => BuildLineOffsets(path, progress));
        Console.WriteLine($"Loaded file in {stopwatch.ElapsedMilliseconds}ms");

        progressWindow.Close();

        _fileHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.RandomAccess);
        _fileLength = RandomAccess.GetLength(_fileHandle);
        _filePath = path;
        _firstLoadedLine = 0;

        LoadWindow(0);
        Scroller.Offset = Vector.Zero;

        Dispatcher.UIThread.Post(() =>
        {
            if (Lines.Count == 0)
            {
                return;
            }

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
            FileTypeFilter = [new FilePickerFileType("Textové soubory") { Patterns = ["*.txt"] }],
        });

        if (files.Count == 0)
        {
            return;
        }

        var localPath = files[0].TryGetLocalPath();
        if (localPath == null)
        {
            return;
        }

        await OpenFile(localPath);
    }

    private async void OnOpenUrlClicked(object? sender, RoutedEventArgs e)
    {
        var url = await ShowUrlInputDialog();
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var statusLabel = new TextBlock
        {
            Text = "Stahování...",
            Margin = new Thickness(20, 20, 20, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var closeBtn = new Button
        {
            Content = "Zavřít",
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };
        var statusWindow = new Window
        {
            Title = "TextReader",
            Width = 300,
            Height = 110,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Children = { statusLabel, closeBtn } },
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
            await OpenFile(tempPath, isTempFile: true);
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
            MinWidth = 360,
        };

        var okBtn = new Button { Content = "Otevřít" };
        var cancelBtn = new Button { Content = "Zrušit" };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12),
            Spacing = 8,
            Children = { okBtn, cancelBtn },
        };

        var dialog = new Window
        {
            Title = "Otevřít z URL",
            Width = 420,
            Height = 120,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Children = { textBox, buttons } },
        };

        string? result = null;
        okBtn.Click += (_, _) =>
        {
            result = textBox.Text;
            dialog.Close();
        };
        cancelBtn.Click += (_, _) => dialog.Close();
        textBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter)
            {
                result = textBox.Text;
                dialog.Close();
            }
            else if (ke.Key == Key.Escape)
            {
                dialog.Close();
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private void NavigateTo(long docLine)
    {
        if (Lines.Count == 0 || Scroller.Extent.Height == 0)
        {
            return;
        }

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
        if (Lines.Count == 0 || Scroller.Extent.Height == 0)
        {
            base.OnKeyDown(e);
            return;
        }

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
                NavigateTo(Math.Max(0, _offsets.Length - (int)(Scroller.Viewport.Height / lineHeight)));
                e.Handled = true;
                break;
            case Key.F3:
                NavigateSearch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : +1);
                e.Handled = true;
                break;
            default:
                base.OnKeyDown(e);
                break;
        }
    }

    private void UpdateCurrentHighlight()
    {
        if (_lastHighlightedItem != null)
        {
            _lastHighlightedItem.CurrentOccurrenceIndex = -1;
            _lastHighlightedItem = null;
        }

        if (_currentResultIndex < 0 || _searchResults.Length == 0)
        {
            return;
        }

        var docLine = _searchResults[_currentResultIndex];
        if (docLine < _firstLoadedLine || docLine >= _firstLoadedLine + Lines.Count)
        {
            return;
        }

        var occIdx = 0;
        for (var i = _currentResultIndex - 1; i >= 0 && _searchResults[i] == docLine; i--)
            occIdx++;

        _lastHighlightedItem = Lines[(int)(docLine - _firstLoadedLine)];
        _lastHighlightedItem.CurrentOccurrenceIndex = occIdx;
    }

    private void LoadWindow(long firstLine)
    {
        firstLine = Math.Clamp(firstLine, 0, Math.Max(0, _offsets.Length - WindowSize));
        _firstLoadedLine = firstLine;
        _lastHighlightedItem = null;

        Lines.Clear();
        var count = (int)Math.Min(WindowSize, _offsets.Length - firstLine);
        for (var i = 0; i < count; i++)
            Lines.Add(new LineItem(ReadLine((int)(firstLine + i)), _activeQuery));

        UpdateCurrentHighlight();
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

        if (Lines.Count == 0)
        {
            return;
        }

        var lineHeight = sv.Extent.Height / Lines.Count;
        var firstVisible = _firstLoadedLine + (int)(sv.Offset.Y / lineHeight);
        var lastVisible = firstVisible + (int)(sv.Viewport.Height / lineHeight);
        var lastLoaded = _firstLoadedLine + Lines.Count - 1;

        _suppressVirtualScroll = true;
        VirtualScroll.Value = firstVisible;
        _suppressVirtualScroll = false;

        if (lastLoaded - lastVisible <= ScrollBuffer)
        {
            if (lastLoaded >= _offsets.Length - 1)
            {
                return;
            }

            const int changeSize = 100;
            var linesToAdd = new LineItem[changeSize];
            for (var i = 0; i < changeSize; i++)
                linesToAdd[i] = new LineItem(ReadLine((int)(lastLoaded + 1 + i)), _activeQuery);

            _adjustingScroll = true;
            Lines.AddRange(linesToAdd);
            _firstLoadedLine += changeSize;
            if (Lines.Count > WindowSize)
            {
                Lines.RemoveRange(0, changeSize);
            }

            UpdateCurrentHighlight();

            Dispatcher.UIThread.Post(() =>
            {
                sv.Offset = sv.Offset.WithY(sv.Offset.Y - changeSize * lineHeight);
                _adjustingScroll = false;
            }, DispatcherPriority.Loaded);
            return;
        }

        var bufferRemainingTop = firstVisible - _firstLoadedLine;
        if (bufferRemainingTop <= ScrollBuffer && _firstLoadedLine > 0)
        {
            const int changeSize = 100;
            var linesToAdd = new LineItem[changeSize];
            for (var i = 0; i < changeSize; i++)
                linesToAdd[i] = new LineItem(ReadLine((int)(_firstLoadedLine - changeSize + i)), _activeQuery);

            _adjustingScroll = true;
            Lines.InsertRange(0, linesToAdd);
            Lines.RemoveRange(Lines.Count - changeSize, changeSize);
            _firstLoadedLine -= changeSize;

            UpdateCurrentHighlight();

            Dispatcher.UIThread.Post(() =>
            {
                sv.Offset = sv.Offset.WithY(sv.Offset.Y + changeSize * lineHeight);
                _adjustingScroll = false;
            }, DispatcherPriority.Loaded);
        }
    }

    private async void OnVirtualScrollChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVirtualScroll)
        {
            return;
        }

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
        {
            return string.Empty;
        }

        var start = _offsets[lineIndex];
        var end = lineIndex + 1 < _offsets.Length ? _offsets[lineIndex + 1] : _fileLength;

        var bytes = new byte[end - start];
        RandomAccess.Read(_fileHandle, bytes, start);

        return Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
    }

    private static long[] BuildLineOffsets(string path, IProgress<double>? progress = null)
    {
        var fileLength = new FileInfo(path).Length;
        var capacity = (int)Math.Min(fileLength / 40 + 1, int.MaxValue);
        var offsets = new long[capacity];
        var count = 1;

        const int bufSize = 1 << 16;//1 << 20;
        var bufA = new byte[bufSize];
        var bufB = new byte[bufSize];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: bufSize,
            options: FileOptions.SequentialScan | FileOptions.Asynchronous);

        var readTask = stream.ReadAsync(bufA, 0, bufSize);
        var processBuf = bufA;
        var readBuf = bufB;
        long position = 0;
        var reportCounter = 0;

        while (true)
        {
            var bytesRead = readTask.GetAwaiter().GetResult();
            if (bytesRead == 0)
            {
                break;
            }

            readTask = stream.ReadAsync(readBuf, 0, bufSize);

            var span = processBuf.AsSpan(0, bytesRead);
            var idx = 0;
            while (true)
            {
                var found = span[idx..].IndexOf((byte)'\n');
                if (found < 0)
                {
                    break;
                }

                idx += found;
                offsets[count++] = position + idx + 1;
                idx++;
            }

            position += bytesRead;

            if (progress != null && ++reportCounter % 10 == 0)
            {
                progress.Report(position * 100.0 / fileLength);
            }

            (processBuf, readBuf) = (readBuf, processBuf);
        }

        progress?.Report(100);
        return offsets[..count];
    }

    // --- Search ---

    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        SearchClearBtn.IsVisible = !string.IsNullOrEmpty(SearchBox.Text);

        _searchDebounceToken?.Cancel();
        _searchDebounceToken = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, _searchDebounceToken.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await StartSearchAsync(SearchBox.Text ?? "");
    }

    private void OnSearchNext(object? sender, RoutedEventArgs e) => NavigateSearch(+1);
    private void OnSearchPrev(object? sender, RoutedEventArgs e) => NavigateSearch(-1);

    private void OnSearchClearClicked(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private void NavigateSearch(int direction)
    {
        if (_searchResults.Length == 0)
        {
            return;
        }

        _currentResultIndex = (_currentResultIndex + direction + _searchResults.Length) % _searchResults.Length;
        SearchStatus.Text = $"{_currentResultIndex + 1} z {_searchResults.Length}";
        NavigateTo(_searchResults[_currentResultIndex]);
    }

    private void ApplySearchQuery(string query)
    {
        _activeQuery = query;
        foreach (var item in Lines)
            item.Query = query;
    }

    private async Task StartSearchAsync(string query)
    {
        _searchCts?.Cancel();
        _searchResults = [];
        _currentResultIndex = -1;

        if (string.IsNullOrWhiteSpace(query) || _filePath == null)
        {
            UpdateCurrentHighlight();
            ApplySearchQuery("");
            SearchStatus.Text = "";
            SearchProgress.IsVisible = false;
            return;
        }

        ApplySearchQuery(query);

        var generation = ++_searchGeneration;
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        SearchProgress.IsVisible = true;
        SearchProgress.Value = 0;
        SearchStatus.Text = "Hledám...";

        var path = _filePath;
        var results = new List<long>();

        var progress = new Progress<(double pct, int found)>(state =>
        {
            if (_searchGeneration != generation)
            {
                return;
            }

            SearchProgress.Value = state.pct;
            SearchStatus.Text = state.found > 0 ? $"... z {state.found}" : "Hledám...";
        });

        try
        {
            await Task.Run(() => SearchInFile(path, query, results, progress, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested || _searchGeneration != generation)
        {
            return;
        }

        _searchResults = results.ToArray();
        _currentResultIndex = _searchResults.Length > 0 ? 0 : -1;
        SearchProgress.IsVisible = false;

        if (_searchResults.Length == 0)
        {
            SearchStatus.Text = "Nenalezeno";
        }
        else
        {
            SearchStatus.Text = $"1 z {_searchResults.Length}";
            NavigateTo(_searchResults[0]);
        }
    }

    private static void SearchInFile(
        string path, string query, List<long> results,
        IProgress<(double pct, int found)> progress, CancellationToken ct)
    {
        var fileLength = new FileInfo(path).Length;
        if (fileLength == 0)
        {
            return;
        }

        const int bufSize = 4 << 20;
        var buf = new byte[bufSize];
        var partialLine = new byte[4096];
        int partialLen = 0;
        long lineNumber = 0;
        long bytesProcessed = 0;
        int bufCount = 0;
        int lastReportedCount = -1;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufSize, FileOptions.SequentialScan);

        while (!ct.IsCancellationRequested)
        {
            var read = stream.Read(buf, 0, bufSize);
            if (read == 0)
            {
                break;
            }

            bytesProcessed += read;
            bufCount++;

            var span = buf.AsSpan(0, read);
            var pos = 0;

            while (pos < span.Length)
            {
                var nl = span[pos..].IndexOf((byte)'\n');
                if (nl < 0)
                {
                    var rest = span[pos..];
                    if (partialLen + rest.Length > partialLine.Length)
                    {
                        Array.Resize(ref partialLine, Math.Max(partialLine.Length * 2, partialLen + rest.Length));
                    }

                    rest.CopyTo(partialLine.AsSpan(partialLen));
                    partialLen += rest.Length;
                    break;
                }

                string lineText;
                if (partialLen > 0)
                {
                    var chunk = span[pos..(pos + nl)];
                    if (partialLen + chunk.Length > partialLine.Length)
                    {
                        Array.Resize(ref partialLine, Math.Max(partialLine.Length * 2, partialLen + chunk.Length));
                    }

                    chunk.CopyTo(partialLine.AsSpan(partialLen));
                    lineText = Encoding.UTF8.GetString(partialLine, 0, partialLen + chunk.Length);
                    partialLen = 0;
                }
                else
                {
                    lineText = Encoding.UTF8.GetString(span[pos..(pos + nl)]);
                }

                var searchFrom = 0;
                while (true)
                {
                    var matchIdx = lineText.IndexOf(query, searchFrom, StringComparison.OrdinalIgnoreCase);
                    if (matchIdx < 0)
                    {
                        break;
                    }

                    results.Add(lineNumber);
                    searchFrom = matchIdx + query.Length;
                }

                lineNumber++;
                pos += nl + 1;
            }

            if (bufCount % 4 == 0 || results.Count > lastReportedCount)
            {
                progress.Report((bytesProcessed * 100.0 / fileLength, results.Count));
                lastReportedCount = results.Count;
            }
        }

        if (partialLen > 0 && !ct.IsCancellationRequested)
        {
            var lineText = Encoding.UTF8.GetString(partialLine, 0, partialLen);
            var searchFrom = 0;
            while (true)
            {
                var matchIdx = lineText.IndexOf(query, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (matchIdx < 0)
                {
                    break;
                }

                results.Add(lineNumber);
                searchFrom = matchIdx + query.Length;
            }
        }

        progress.Report((100.0, results.Count));
    }
}