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

    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromRgb(255, 220, 0));

    static HighlightTextBlock()
    {
        LineTextProperty.Changed.AddClassHandler<HighlightTextBlock>((b, _) => b.Rebuild());
        QueryProperty.Changed.AddClassHandler<HighlightTextBlock>((b, _) => b.Rebuild());
    }

    private void Rebuild()
    {
        Inlines!.Clear();
        var text = LineText ?? "";
        var query = Query;

        if (string.IsNullOrEmpty(query))
        {
            Inlines.Add(new Run(text));
            return;
        }

        var pos = 0;
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

            Inlines.Add(new Run(text[idx..(idx + query.Length)]) { Background = HighlightBrush });

            pos = idx + query.Length;
        }
    }
}
