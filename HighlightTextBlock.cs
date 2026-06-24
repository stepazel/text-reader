using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace TextReader;

public class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> LineTextProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(LineText));

    public static readonly StyledProperty<string?> QueryProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(Query));

    public static readonly StyledProperty<int> CurrentOccurrenceIndexProperty =
        AvaloniaProperty.Register<HighlightTextBlock, int>(nameof(CurrentOccurrenceIndex), defaultValue: -1);

    public string? LineText
    {
        get => GetValue(LineTextProperty);
        set => SetValue(LineTextProperty, value);
    }

    public string? Query
    {
        get => GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public int CurrentOccurrenceIndex
    {
        get => GetValue(CurrentOccurrenceIndexProperty);
        set => SetValue(CurrentOccurrenceIndexProperty, value);
    }

    // Current result: bright yellow fill
    private static readonly IBrush CurrentBrush = new SolidColorBrush(Color.FromRgb(255, 220, 0));
    // Other results: subtle blue tint (like VS Code other-match highlight)
    private static readonly IBrush OtherBrush = new SolidColorBrush(Color.FromRgb(180, 210, 255));

    static HighlightTextBlock()
    {
        LineTextProperty.Changed.AddClassHandler<HighlightTextBlock>((b, _) => b.Rebuild());
        QueryProperty.Changed.AddClassHandler<HighlightTextBlock>((b, _) => b.Rebuild());
        CurrentOccurrenceIndexProperty.Changed.AddClassHandler<HighlightTextBlock>((b, _) => b.Rebuild());
    }

    private void Rebuild()
    {
        Inlines!.Clear();
        var text = LineText ?? "";
        var query = Query;

        if (string.IsNullOrEmpty(query) || text.Length == 0)
        {
            Inlines.Add(new Run(text));
            return;
        }

        var pos = 0;
        var occIdx = 0;
        var currentOcc = CurrentOccurrenceIndex;

        while (pos < text.Length)
        {
            var idx = text.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                Inlines.Add(new Run(text[pos..]));
                break;
            }

            if (idx > pos)
                Inlines.Add(new Run(text[pos..idx]));

            Inlines.Add(new Run(text[idx..(idx + query.Length)])
            {
                Background = occIdx == currentOcc ? CurrentBrush : OtherBrush
            });

            occIdx++;
            pos = idx + query.Length;
        }
    }
}
