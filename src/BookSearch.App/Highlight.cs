using BookSearch.App.Ask;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace BookSearch.App;

/// <summary>
/// <c>local:Highlight.Source="{x:Bind Excerpt}"</c> fills a TextBlock with the text, the matched terms semibold and
/// underlined (so they don't depend on colour), and sets the reading direction for right-to-left languages.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(HighlightedText), typeof(Highlight), new PropertyMetadata(null, OnSourceChanged));

    public static HighlightedText? GetSource(DependencyObject element) => (HighlightedText?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, HighlightedText? value) => element.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        if (e.NewValue is not HighlightedText source) return;

        block.FlowDirection = source.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var text = source.Text;
        var position = 0;
        foreach (var span in source.Spans.OrderBy(s => s.Start))
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
    }
}
