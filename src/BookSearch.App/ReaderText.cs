using BookSearch.App.Reader;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace BookSearch.App;

/// <summary>
/// <c>local:ReaderText.Source="{x:Bind}"</c> shows one paragraph of a chapter in a TextBlock: the opened passage on a
/// soft background, the question's rare words semibold and underlined, and right-to-left reading order where the text needs it.
/// </summary>
public static class ReaderText
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(ReaderParagraph), typeof(ReaderText), new PropertyMetadata(null, OnSourceChanged));

    public static ReaderParagraph? GetSource(DependencyObject element) => (ReaderParagraph?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, ReaderParagraph? value) => element.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.TextHighlighters.Clear();
        block.Inlines.Clear();
        if (e.NewValue is not ReaderParagraph paragraph) return;

        var text = paragraph.Text;
        var position = 0;
        foreach (var span in paragraph.TermSpans.OrderBy(s => s.Start))
        {
            var start = Math.Clamp(span.Start, position, text.Length);
            var end = Math.Clamp(span.End, start, text.Length);
            if (start > position) block.Inlines.Add(new Run { Text = text[position..start] });
            if (end > start)
            {
                var matched = new Underline();
                matched.Inlines.Add(new Run { Text = text[start..end], FontWeight = FontWeights.SemiBold });
                block.Inlines.Add(matched);
            }
            position = end;
        }
        if (position < text.Length) block.Inlines.Add(new Run { Text = text[position..] });
        block.FlowDirection = paragraph.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        if (paragraph.PassageSpan is { } passage)
        {
            var highlighter = new TextHighlighter { Background = ThemeBrush("SystemFillColorCautionBackgroundBrush", Windows.UI.Color.FromArgb(0x55, 0xFF, 0xC8, 0x3D)) };
            highlighter.Ranges.Add(new TextRange { StartIndex = passage.Start, Length = passage.Length });
            block.TextHighlighters.Add(highlighter);
        }
    }

    /// <summary>A theme brush, or a fixed colour if this Windows build doesn't define it (a missing key must not break reading).</summary>
    private static Brush ThemeBrush(string key, Windows.UI.Color fallback)
    {
        try { return (Brush)Application.Current.Resources[key]; }
        catch (Exception e) when (e is KeyNotFoundException or InvalidCastException or System.Runtime.InteropServices.COMException)
        {
            return new SolidColorBrush(fallback);
        }
    }
}
