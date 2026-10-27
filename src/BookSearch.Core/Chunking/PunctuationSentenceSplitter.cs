namespace BookSearch.Core.Chunking;

/// <summary>
/// Fallback splitter that needs no native library. Breaks after . ! ? … and the full-width and Arabic
/// equivalents, except after common abbreviations, initials and before a lower-case word.
/// </summary>
public sealed class PunctuationSentenceSplitter : ISentenceSplitter
{
    private const string Closers = "\"'”’)]}»」』）";

    public IReadOnlyList<(int Start, int Length)> Split(string text, string? language)
    {
        var ends = new List<(int, int)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (!IsTerminator(ch)) continue;

            var j = i + 1;
            while (j < text.Length && IsTerminator(text[j])) j++;          // "?!", "..."
            var closers = j;
            while (j < text.Length && Closers.Contains(text[j])) j++;      // closing quotes and brackets

            var wide = IsWideTerminator(ch);
            var atEnd = j >= text.Length;
            if (!wide && !atEnd && !char.IsWhiteSpace(text[j])) { i = j - 1; continue; }

            if (!wide && ch == '.' && !atEnd && !AbbreviationRules.IsBoundaryAfterPeriod(text, i, j)) { i = j - 1; continue; }

            // '"Where are you going?" she asked.' - a quoted question followed by a lower-case attribution.
            if (!wide && ch != '.' && j > closers && !atEnd && StartsLowerCase(text, j)) { i = j - 1; continue; }

            ends.Add((start, j));
            start = j;
            i = j - 1;
        }
        if (start < text.Length) ends.Add((start, text.Length));
        return SentenceSplitters.Trim(text, ends);
    }

    private static bool StartsLowerCase(string text, int from)
    {
        while (from < text.Length && char.IsWhiteSpace(text[from])) from++;
        return from < text.Length && char.IsLower(text[from]);
    }

    private static bool IsTerminator(char ch) => ch is '.' or '!' or '?' or '…' or '。' or '！' or '？' or '؟' or '‼' or '⁇' or '।';

    /// <summary>Terminators that end a sentence even with no space after them.</summary>
    private static bool IsWideTerminator(char ch) => ch is '。' or '！' or '？';
}
