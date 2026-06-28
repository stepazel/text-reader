using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Win32.SafeHandles;

namespace TextReader;

public partial class MainWindow : Window
{
    private const int WindowSize = 500;
    private const int ScrollBuffer = 100;

    private enum FileSource { FileSystem, Url, Generated }

    private long[] _offsets = [];
    private long _fileLength;
    private SafeFileHandle? _fileHandle;
    private string? _tempFile;
    private string? _filePath;
    private FileSource _fileSource;
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
    
    private CancellationTokenSource? _scrollCts;
    private bool _isDraggingVirtualScroll;
    private double _charWidth;

    private bool _filterMode;
    private long[] _filterDocLines = [];

    // In filter mode the "index space" is _filterDocLines; in normal mode it's _offsets.
    private long IndexLength => _filterMode ? _filterDocLines.Length : _offsets.Length;
    private string LoadLineAt(long index) => _filterMode ? ReadLine((int)_filterDocLines[index]) : ReadLine((int)index);
    private long DocLineAt(long index) => _filterMode ? _filterDocLines[index] : index;


    public AvaloniaList<LineItem> Lines { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Scroller.Focus();
        NavigateToLineBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (long.TryParse(NavigateToLineBox.Text, out var line) && line >= 1 && _offsets.Length > 0)
            {
                ExitFilterMode();
                NavigateTo(Math.Min(line - 1, _offsets.Length - 1));
            }
            NavigateToLineBox.Text = "";
            Scroller.Focus();
            e.Handled = true;
        };
        
        Scroller.AddHandler(
            KeyDownEvent,
            (_, e) => OnKeyDown(e),
            RoutingStrategies.Tunnel);
        VirtualScroll.AddHandler(Thumb.DragStartedEvent, (_, _) =>
        {
            if (IndexLength <= WindowSize) return;
            _isDraggingVirtualScroll = true;
            _debounceToken?.Cancel();
            _scrollCts?.Cancel();
        }, RoutingStrategies.Bubble);
        VirtualScroll.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
        {
            if (!_isDraggingVirtualScroll) return;
            _isDraggingVirtualScroll = false;
            ScrollHint.IsVisible = false;
            NavigateTo((long)VirtualScroll.Value);
        }, RoutingStrategies.Bubble);
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

    private async Task OpenFile(string path, FileSource source = FileSource.FileSystem)
    {
        _searchCts?.Cancel();
        _filterMode = false;
        _filterDocLines = [];
        _searchResults = [];
        _currentResultIndex = -1;
        SearchStatus.Text = "";
        SearchProgress.IsVisible = false;
        FilterButton.IsVisible = false;
        FilterButton.Content = "Filtrovat";

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

        _fileSource = source;
        _tempFile = source != FileSource.FileSystem ? path : null;

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
            SaveButton.IsVisible = _tempFile != null;
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
            await OpenFile(tempPath, FileSource.Url);
        }
        catch
        {
            statusLabel.Text = "Vyskytla se chyba při stahování";
            closeBtn.IsVisible = true;
        }
    }

    private async void OnGenerateClicked(object? sender, RoutedEventArgs e)
    {
        var lineCountBox = new TextBox
        {
            Text = "500000",
            Margin = new Thickness(12, 12, 12, 8),
            MinWidth = 200,
        };

        var okBtn = new Button { Content = "Generovat" };
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
            Title = "Generovat lorem ipsum",
            Width = 280,
            Height = 120,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Počet řádků:", Margin = new Thickness(12, 12, 12, 0) },
                    lineCountBox,
                    buttons,
                },
            },
        };

        int? lineCount = null;
        okBtn.Click += (_, _) =>
        {
            if (int.TryParse(lineCountBox.Text, out var n) && n > 0)
            {
                lineCount = n;
                dialog.Close();
            }
        };
        cancelBtn.Click += (_, _) => dialog.Close();
        lineCountBox.KeyDown += (_, ke) =>
        {
            if (ke.Key != Key.Enter) return;
            if (int.TryParse(lineCountBox.Text, out var n) && n > 0)
            {
                lineCount = n;
                dialog.Close();
            }
        };

        await dialog.ShowDialog(this);
        if (lineCount is not { } count) return;

        var progressBar = new ProgressBar
            { Minimum = 0, Maximum = 100, Value = 0, Width = 260, Margin = new Thickness(16, 16, 16, 8) };
        var progressLabel = new TextBlock
            { Text = "0 %", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
        var progressWindow = new Window
        {
            Title = "Generování...",
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

        var tempPath = Path.GetTempFileName();
        try
        {
            await Task.Run(() => GenerateLoremIpsum(tempPath, count, progress));
        }
        catch
        {
            progressWindow.Close();
            try { File.Delete(tempPath); } catch { /* ignore */ }
            return;
        }

        progressWindow.Close();
        await OpenFile(tempPath, FileSource.Generated);
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_filePath == null) return;

        var suggestedName = _fileSource == FileSource.Generated ? "generated.txt" : "downloaded.txt";

        var dest = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Uložit soubor",
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType("Textový soubor") { Patterns = ["*.txt"] }],
        });

        if (dest?.TryGetLocalPath() is not { } destPath) return;

        try
        {
            File.Copy(_filePath, destPath, overwrite: true);
        }
        catch (Exception ex)
        {
            var errWindow = new Window
            {
                Title = "Chyba",
                Width = 340,
                Height = 110,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = $"Nepodařilo se uložit soubor: {ex.Message}", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right },
                    },
                },
            };
            var okBtn = (Button)((StackPanel)errWindow.Content!).Children[1];
            okBtn.Click += (_, _) => errWindow.Close();
            await errWindow.ShowDialog(this);
        }
    }

    private static readonly string[] LoremWords =
    [
        "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit",
        "sed", "do", "eiusmod", "tempor", "incididunt", "ut", "labore", "et", "dolore",
        "magna", "aliqua", "enim", "ad", "minim", "veniam", "quis", "nostrud",
        "exercitation", "ullamco", "laboris", "nisi", "aliquip", "ex", "ea", "commodo",
        "consequat", "duis", "aute", "irure", "reprehenderit", "voluptate", "velit",
        "esse", "cillum", "fugiat", "nulla", "pariatur", "excepteur", "sint", "occaecat",
        "cupidatat", "non", "proident", "sunt", "culpa", "qui", "officia", "deserunt",
        "mollit", "anim", "id", "est", "laborum",
    ];

    private static void GenerateLoremIpsum(string path, int lineCount, IProgress<double> progress)
    {
        var rng = new Random();
        const int bufSize = 1 << 16;
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8, bufSize);

        for (var i = 1; i <= lineCount; i++)
        {
            writer.Write(i);
            writer.Write(": ");
            var wordCount = rng.Next(5, 16);
            for (var w = 0; w < wordCount; w++)
            {
                if (w > 0) writer.Write(' ');
                writer.Write(LoremWords[rng.Next(LoremWords.Length)]);
            }
            writer.WriteLine();

            if (i % 10000 == 0)
                progress.Report(i * 100.0 / lineCount);
        }
        progress.Report(100);
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

    private void NavigateTo(long docLine, double targetX = 0)
    {
        if (Lines.Count == 0 || Scroller.Extent.Height == 0)
        {
            return;
        }

        var lineHeight = Scroller.Extent.Height / Lines.Count;
        docLine = Math.Clamp(docLine, 0, IndexLength - 1);
        LoadWindow(Math.Max(0, docLine - WindowSize / 2));

        var lineInBuffer = docLine - _firstLoadedLine;
        _adjustingScroll = true;
        Dispatcher.UIThread.Post(() =>
        {
            Scroller.Offset = new Vector(targetX, lineInBuffer * lineHeight);
            _adjustingScroll = false;
            _suppressVirtualScroll = true;
            VirtualScroll.Value = docLine;
            _suppressVirtualScroll = false;
        }, DispatcherPriority.Loaded);
    }

    private async void GoTo(long docLine)
    {
        docLine = Math.Clamp(docLine, 0, Math.Max(0, IndexLength - 1));
        if (docLine >= _firstLoadedLine && docLine < _firstLoadedLine + Lines.Count)
        {
            SmoothScrollTo(Scroller, (docLine - _firstLoadedLine) * (Scroller.Extent.Height / Lines.Count));
            return;
        }

        _scrollCts?.Cancel();
        _scrollCts = new CancellationTokenSource();
        var token = _scrollCts.Token;

        try
        {
            var scrollbarTask = AnimateScrollBar(VirtualScroll.Value, docLine, 210, token);
            await AnimateOpacity(Scroller, 1.0, 0.0, 150, token);
            NavigateTo(docLine);
            await Task.WhenAll(Task.Delay(60, token), scrollbarTask);
            await AnimateOpacity(Scroller, 0.0, 1.0, 200, token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            Scroller.Opacity = 1.0;
        }
    }

    private async Task AnimateScrollBar(double from, double to, int durationMs, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var easing = new CubicEaseInOut();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            var t = sw.ElapsedMilliseconds / (double)durationMs;
            _suppressVirtualScroll = true;
            VirtualScroll.Value = from + (to - from) * easing.Ease(t);
            _suppressVirtualScroll = false;
            await Task.Delay(16, token);
        }
        _suppressVirtualScroll = true;
        VirtualScroll.Value = to;
        _suppressVirtualScroll = false;
    }

    private static async Task AnimateOpacity(Control control, double from, double to, int durationMs, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var easing = new CubicEaseOut();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            var t = sw.ElapsedMilliseconds / (double)durationMs;
            control.Opacity = from + (to - from) * easing.Ease(t);
            await Task.Delay(16, token);
        }
        control.Opacity = to;
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
                SmoothScrollTo(Scroller, Scroller.Offset.Y + Scroller.Viewport.Height);
                e.Handled = true;
                break;
            case Key.PageUp:
                SmoothScrollTo(Scroller, Scroller.Offset.Y - Scroller.Viewport.Height);
                e.Handled = true;
                break;
            case Key.Home:
                GoTo(0);
                e.Handled = true;
                break;
            case Key.End:
                GoTo(Math.Max(0, IndexLength - (int)(Scroller.Viewport.Height / lineHeight)));
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
    
    private void SmoothScrollTo(ScrollViewer sv, double targetY)
    {
        _scrollCts?.Cancel();
        _scrollCts = new CancellationTokenSource();
        var token = _scrollCts.Token;

        var startY = sv.Offset.Y;
        var endY = Math.Clamp(targetY, 0, sv.ScrollBarMaximum.Y);
        if (Math.Abs(endY - startY) < 1) return;

        var sw = Stopwatch.StartNew();
        const double durationMs = 350;
        var easing = new CubicEaseOut();

        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        timer.Tick += (_, _) =>
        {
            if (token.IsCancellationRequested)
            {
                timer.Stop();
                return;
            }

            var progress = sw.Elapsed.TotalMilliseconds / durationMs;
            if (progress >= 1.0)
            {
                sv.Offset = new Vector(sv.Offset.X, endY);
                timer.Stop();
                return;
            }

            sv.Offset = new Vector(sv.Offset.X, startY + (endY - startY) * easing.Ease(progress));
        };

        timer.Start();
    }


    private void UpdateCurrentHighlight()
    {
        if (_lastHighlightedItem != null)
        {
            _lastHighlightedItem.CurrentOccurrenceIndex = -1;
            _lastHighlightedItem = null;
        }

        if (_currentResultIndex < 0 || _searchResults.Length == 0) return;

        var docLine = _searchResults[_currentResultIndex];

        long bufferPos;
        if (_filterMode)
        {
            var fi = Array.BinarySearch(_filterDocLines, docLine);
            if (fi < 0 || fi < _firstLoadedLine || fi >= _firstLoadedLine + Lines.Count) return;
            bufferPos = fi - _firstLoadedLine;
        }
        else
        {
            if (docLine < _firstLoadedLine || docLine >= _firstLoadedLine + Lines.Count) return;
            bufferPos = docLine - _firstLoadedLine;
        }

        var occIdx = 0;
        for (var i = _currentResultIndex - 1; i >= 0 && _searchResults[i] == docLine; i--)
            occIdx++;

        _lastHighlightedItem = Lines[(int)bufferPos];
        _lastHighlightedItem.CurrentOccurrenceIndex = occIdx;
    }

    private void LoadWindow(long firstLine)
    {
        firstLine = Math.Clamp(firstLine, 0, Math.Max(0, IndexLength - WindowSize));
        _firstLoadedLine = firstLine;
        _lastHighlightedItem = null;

        Lines.Clear();
        var count = (int)Math.Min(WindowSize, IndexLength - firstLine);
        for (var i = 0; i < count; i++)
        {
            var idx = firstLine + i;
            Lines.Add(new LineItem(LoadLineAt(idx), _activeQuery, DocLineAt(idx)));
        }

        UpdateCurrentHighlight();
    }

    private void LoadDragView(long firstLine)
    {
        firstLine = Math.Clamp(firstLine, 0, Math.Max(0, IndexLength - 1));
        if (firstLine == _firstLoadedLine && Lines.Count > 0) return;

        _firstLoadedLine = firstLine;
        _lastHighlightedItem = null;

        var visibleCount = Math.Max(1, (int)VirtualScroll.ViewportSize) + 2;
        var count = (int)Math.Min(visibleCount, IndexLength - firstLine);
        var items = new LineItem[count];
        for (var i = 0; i < count; i++)
        {
            var idx = firstLine + i;
            items[i] = new LineItem(LoadLineAt(idx), _activeQuery, DocLineAt(idx));
        }

        _adjustingScroll = true;
        Lines.Clear();
        Lines.AddRange(items);
        Dispatcher.UIThread.Post(() =>
        {
            Scroller.Offset = Vector.Zero;
            _adjustingScroll = false;
        }, DispatcherPriority.Loaded);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || _adjustingScroll || _isDraggingVirtualScroll)
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

        LineNumberScroller.Offset = LineNumberScroller.Offset.WithY(sv.Offset.Y);

        var lineHeight = sv.Extent.Height / Lines.Count;
        var firstVisible = _firstLoadedLine + (int)(sv.Offset.Y / lineHeight);
        var lastVisible = firstVisible + (int)(sv.Viewport.Height / lineHeight);
        var lastLoaded = _firstLoadedLine + Lines.Count - 1;

        _suppressVirtualScroll = true;
        VirtualScroll.Value = firstVisible;
        _suppressVirtualScroll = false;

        if (lastLoaded - lastVisible <= ScrollBuffer)
        {
            if (lastLoaded >= IndexLength - 1)
            {
                return;
            }

            const int changeSize = 100;
            var linesToAdd = new LineItem[changeSize];
            for (var i = 0; i < changeSize; i++)
            {
                var idx = lastLoaded + 1 + i;
                linesToAdd[i] = new LineItem(LoadLineAt(idx), _activeQuery, DocLineAt(idx));
            }

            _scrollCts?.Cancel();
            _adjustingScroll = true;
            Lines.AddRange(linesToAdd);
            var removedFromTop = Lines.Count > WindowSize;
            if (removedFromTop)
            {
                Lines.RemoveRange(0, changeSize);
                _firstLoadedLine += changeSize;
            }

            UpdateCurrentHighlight();

            Dispatcher.UIThread.Post(() =>
            {
                if (removedFromTop)
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
            {
                var idx = _firstLoadedLine - changeSize + i;
                linesToAdd[i] = new LineItem(LoadLineAt(idx), _activeQuery, DocLineAt(idx));
            }

            _scrollCts?.Cancel();
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
        if (_suppressVirtualScroll) return;

        if (IndexLength > 0 && IndexLength <= WindowSize)
        {
            if (Lines.Count == 0 || Scroller.Extent.Height == 0) return;
            var lineHeight = Scroller.Extent.Height / Lines.Count;
            _adjustingScroll = true;
            Scroller.Offset = Scroller.Offset.WithY(e.NewValue * lineHeight);
            Dispatcher.UIThread.Post(() => _adjustingScroll = false, DispatcherPriority.Loaded);
            return;
        }

        if (_isDraggingVirtualScroll)
        {
            var line = (long)e.NewValue;
            var pct = IndexLength > 0 ? line * 100.0 / IndexLength : 0;
            ScrollHintText.Text = _filterMode
                ? $"Výsledek {line + 1} / {IndexLength}  (řádek {DocLineAt(Math.Min(line, IndexLength - 1)) + 1})"
                : $"Řádek {line + 1}  ({pct:0.0} %)";
            var hintY = VirtualScroll.Maximum > 0
                ? e.NewValue / VirtualScroll.Maximum * Math.Max(0, VirtualScroll.Bounds.Height - 40)
                : 0;
            ScrollHint.Margin = new Thickness(0, hintY, 4, 0);
            ScrollHint.IsVisible = true;
            LoadDragView(line);
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

        GoTo((long)e.NewValue);
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
        var searching = SearchProgress.IsVisible ? " ..." : "";
        SearchStatus.Text = $"{_currentResultIndex + 1} z {_searchResults.Length}{searching}";
        var docLine = _searchResults[_currentResultIndex];
        if (_filterMode)
        {
            var fi = Array.BinarySearch(_filterDocLines, docLine);
            if (fi >= 0) NavigateTo(fi, ComputeMatchScrollX(docLine));
        }
        else
        {
            NavigateTo(docLine, ComputeMatchScrollX(docLine));
        }
    }

    private double ComputeMatchScrollX(long docLine)
    {
        if (string.IsNullOrEmpty(_activeQuery)) return 0;

        var occIdx = 0;
        for (var i = _currentResultIndex - 1; i >= 0 && _searchResults[i] == docLine; i--)
            occIdx++;

        var lineText = ReadLine((int)docLine);
        var charPos = 0;
        var found = 0;
        while (true)
        {
            var idx = lineText.IndexOf(_activeQuery, charPos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;
            if (found == occIdx)
            {
                var cw = GetCharWidth();
                var matchStartX = idx * cw;
                var matchWidth = _activeQuery.Length * cw;
                var centerX = matchStartX - (Scroller.Viewport.Width - matchWidth) / 2;
                return Math.Max(0, centerX);
            }
            found++;
            charPos = idx + _activeQuery.Length;
        }
    }

    private double GetCharWidth()
    {
        if (_charWidth > 0) return _charWidth;
        var ft = new FormattedText(
            "X",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Courier New"),
            13,
            Brushes.Black);
        _charWidth = ft.Width;
        return _charWidth;
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

        if (_filterMode)
        {
            _filterMode = false;
            _filterDocLines = [];
            FilterButton.Content = "Filtrovat";
            // Restore full view immediately so user isn't stuck in filter while search runs.
            var restoreDoc = _currentResultIndex >= 0 && _searchResults.Length > 0
                ? _searchResults[_currentResultIndex] : 0L;
            _firstLoadedLine = 0;
            LoadWindow(0);
            Scroller.Offset = Vector.Zero;
            Dispatcher.UIThread.Post(() =>
            {
                if (Lines.Count == 0 || Scroller.Extent.Height == 0) return;
                var lh = Scroller.Extent.Height / Lines.Count;
                var pl = (int)(Scroller.Viewport.Height / lh);
                _suppressVirtualScroll = true;
                VirtualScroll.Maximum = Math.Max(0, _offsets.Length - pl);
                VirtualScroll.ViewportSize = pl;
                VirtualScroll.Value = restoreDoc;
                _suppressVirtualScroll = false;
            }, DispatcherPriority.Loaded);
        }

        _searchResults = [];
        _currentResultIndex = -1;
        FilterButton.IsVisible = false;

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

        var progress = new Progress<(double pct, long[]? snapshot)>(state =>
        {
            if (_searchGeneration != generation) return;
            SearchProgress.Value = state.pct;
            if (state.snapshot == null) return;
            _searchResults = state.snapshot;
            SearchStatus.Text = _currentResultIndex >= 0
                ? $"{_currentResultIndex + 1} z {state.snapshot.Length} ..."
                : (state.snapshot.Length > 0 ? $"... z {state.snapshot.Length}" : "Hledám...");
        });

        try
        {
            await Task.Run(() => SearchInFile(path, query, results, progress, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested || _searchGeneration != generation) return;

        _searchResults = results.ToArray();
        SearchProgress.IsVisible = false;
        FilterButton.IsVisible = _searchResults.Length > 0;

        if (_searchResults.Length == 0)
        {
            _currentResultIndex = -1;
            SearchStatus.Text = "Nenalezeno";
        }
        else if (_currentResultIndex < 0)
        {
            _currentResultIndex = 0;
            SearchStatus.Text = $"1 z {_searchResults.Length}";
            NavigateTo(_searchResults[0], ComputeMatchScrollX(_searchResults[0]));
        }
        else
        {
            _currentResultIndex = Math.Min(_currentResultIndex, _searchResults.Length - 1);
            SearchStatus.Text = $"{_currentResultIndex + 1} z {_searchResults.Length}";
        }
    }

    private static void SearchInFile(
        string path, string query, List<long> results,
        IProgress<(double pct, long[]? snapshot)> progress, CancellationToken ct)
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
                long[]? snapshot = results.Count > lastReportedCount ? results.ToArray() : null;
                progress.Report((bytesProcessed * 100.0 / fileLength, snapshot));
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

        progress.Report((100.0, null));
    }

    private void ExitFilterMode()
    {
        if (!_filterMode) return;
        var docLine = _currentResultIndex >= 0 && _searchResults.Length > 0
            ? _searchResults[_currentResultIndex] : 0L;
        _filterMode = false;
        _filterDocLines = [];
        FilterButton.Content = "Filtrovat";
        NavigateTo(docLine);
        Dispatcher.UIThread.Post(() =>
        {
            if (Lines.Count == 0 || Scroller.Extent.Height == 0) return;
            var lh = Scroller.Extent.Height / Lines.Count;
            var pl = (int)(Scroller.Viewport.Height / lh);
            _suppressVirtualScroll = true;
            VirtualScroll.Maximum = Math.Max(0, _offsets.Length - pl);
            VirtualScroll.SmallChange = 1;
            VirtualScroll.LargeChange = pl;
            VirtualScroll.ViewportSize = pl;
            VirtualScroll.Value = docLine;
            _suppressVirtualScroll = false;
        }, DispatcherPriority.Loaded);
    }

    private void OnFilterToggled(object? sender, RoutedEventArgs e)
    {
        if (_filterMode)
        {
            ExitFilterMode();
            return;
        }

        if (_searchResults.Length == 0 || SearchProgress.IsVisible) return;

        _filterDocLines = _searchResults.Distinct().ToArray();
        _filterMode = true;
        FilterButton.Content = "Vše";

        var filterIdx = _currentResultIndex >= 0
            ? Math.Max(0L, Array.BinarySearch(_filterDocLines, _searchResults[_currentResultIndex]))
            : 0L;

        NavigateTo(filterIdx);
        Dispatcher.UIThread.Post(() =>
        {
            if (Lines.Count == 0 || Scroller.Extent.Height == 0) return;
            var lh = Scroller.Extent.Height / Lines.Count;
            var pl = (int)(Scroller.Viewport.Height / lh);
            _suppressVirtualScroll = true;
            VirtualScroll.Maximum = Math.Max(0, _filterDocLines.Length - pl);
            VirtualScroll.SmallChange = 1;
            VirtualScroll.LargeChange = pl;
            VirtualScroll.ViewportSize = pl;
            VirtualScroll.Value = filterIdx;
            _suppressVirtualScroll = false;
        }, DispatcherPriority.Loaded);
    }
}