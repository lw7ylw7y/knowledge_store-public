using System.Text.RegularExpressions;

namespace BookSearch.Core.Search;

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>Finds the query terms in a passage and cuts a short, readable excerpt around the best cluster of them.</summary>
public static partial class ExcerptBuilder
{
    /// <summary>
    /// Case-insensitive matches of the query terms, merged where they overlap. Word terms match whole words;
    /// trigrams match anywhere.
    /// </summary>
    public static List<TextSpan> FindMatches(string text, QueryTerms terms)
    {
        var spans = new List<TextSpan>();
        foreach (var word in terms.Words)
            for (var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                var end = at + word.Length;
                var startsWord = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
                var endsWord = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (startsWord && endsWord) spans.Add(new TextSpan(at, word.Length));
            }
        foreach (var gram in terms.Trigrams)
            for (var at = text.IndexOf(gram, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = text.IndexOf(gram, at + 1, StringComparison.OrdinalIgnoreCase))
                spans.Add(new TextSpan(at, gram.Length));

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<TextSpan>();
        foreach (var s in spans)
            if (merged.Count > 0 && s.Start <= merged[^1].End)
                merged[^1] = new TextSpan(merged[^1].Start, Math.Max(merged[^1].End, s.End) - merged[^1].Start);
            else merged.Add(s);
        return merged;
    }

    /// <summary>
    /// An excerpt of at most about <paramref name="maxChars"/> characters starting near the densest run of matches
    /// (or at the start when nothing matches), with highlight spans relative to the returned excerpt. All of
    /// <paramref name="terms"/> decide where the excerpt starts; only <paramref name="highlightTerms"/> (default: all) are marked.
    /// </summary>
    public static (string Excerpt, IReadOnlyList<TextSpan> Highlights) Build(
        string passage, QueryTerms terms, int maxChars = 320, QueryTerms? highlightTerms = null)
    {
        var text = Whitespace().Replace(passage, " ").Trim();
        var matches = FindMatches(text, terms);
        var marked = highlightTerms is null ? matches : FindMatches(text, highlightTerms);
        if (text.Length <= maxChars) return (text, marked);

        // Window start: the match that has the most other matches within one window after it.
        var anchor = 0;
        var best = -1;
        foreach (var m in matches)
        {
            var count = matches.Count(o => o.Start >= m.Start && o.End <= m.Start + maxChars);
            if (count > best) { best = count; anchor = m.Start; }
        }

        var start = Math.Max(0, anchor - 40);                    // a little lead-in before the first match
        if (start > 0)
        {
            var space = text.IndexOf(' ', start);
            if (space >= 0 && space - start < 30 && space < anchor) start = space + 1;
        }
        var end = Math.Min(text.Length, start + maxChars);
        if (end < text.Length)
        {
            var space = text.LastIndexOf(' ', end - 1, Math.Min(30, end - start));
            if (space > start) end = space;
        }

        var prefix = start > 0 ? "… " : "";
        var suffix = end < text.Length ? " …" : "";
        var excerpt = prefix + text[start..end] + suffix;
        var highlights = marked
            .Where(m => m.Start >= start && m.End <= end)
            .Select(m => new TextSpan(m.Start - start + prefix.Length, m.Length))
            .ToList();
        return (excerpt, highlights);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
