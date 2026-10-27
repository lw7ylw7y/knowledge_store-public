namespace BookSearch.Core.Chunking;

public interface ISentenceSplitter
{
    /// <summary>Sentence spans (start, length) over <paramref name="text"/>, trimmed of surrounding whitespace.</summary>
    IReadOnlyList<(int Start, int Length)> Split(string text, string? language);
}

public static class SentenceSplitters
{
    /// <summary>ICU's break iterator when the platform has it, otherwise the punctuation-based splitter.</summary>
    public static ISentenceSplitter CreateDefault() =>
        IcuSentenceSplitter.TryCreate() ?? (ISentenceSplitter)new PunctuationSentenceSplitter();

    /// <summary>
    /// Joins a sentence back onto the next one when it only ended at an abbreviation or initial. ICU's sentence
    /// rules keep "Mr. Jones" together but still break after "Dr." and between "J. K.".
    /// </summary>
    internal static List<(int Start, int Length)> MergeAbbreviations(string text, List<(int Start, int Length)> spans)
    {
        var merged = new List<(int Start, int Length)>();
        foreach (var span in spans)
        {
            if (merged.Count > 0)
            {
                var (pStart, pLength) = merged[^1];
                var last = pStart + pLength - 1;
                if ((text[last] == '.' && AbbreviationRules.EndsWithNonTerminalWord(text, last))
                    || IsQuotedQuestionContinued(text, pStart, last, span.Start))
                {
                    merged[^1] = (pStart, span.Start + span.Length - pStart);
                    continue;
                }
            }
            merged.Add(span);
        }
        return merged;
    }

    /// <summary>'"Where are you going?" she asked.': ICU breaks after the quoted question, but the attribution continues it.</summary>
    private static bool IsQuotedQuestionContinued(string text, int start, int last, int nextStart)
    {
        const string closers = "\"'”’)]}»」』）";
        var i = last;
        var sawCloser = false;
        while (i >= start && closers.Contains(text[i])) { i--; sawCloser = true; }
        return sawCloser && i >= start && text[i] is '?' or '!' && char.IsLower(text[nextStart]);
    }

    internal static List<(int Start, int Length)> Trim(string text, IEnumerable<(int Start, int End)> raw)
    {
        var spans = new List<(int, int)>();
        foreach (var (start, end) in raw)
        {
            var s = start;
            var e = end;
            while (s < e && char.IsWhiteSpace(text[s])) s++;
            while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
            if (e > s) spans.Add((s, e - s));
        }
        return spans;
    }
}
