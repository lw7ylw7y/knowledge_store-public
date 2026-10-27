namespace BookSearch.Core.Chunking;

/// <summary>Where a period does not end a sentence: abbreviations ("Dr."), initials ("J. K.") and "etc. and".</summary>
internal static class AbbreviationRules
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "mt", "vs", "etc", "e.g", "i.e", "cf", "no", "fig",
        "vol", "inc", "ltd", "co", "gen", "col", "lt", "capt", "rev", "hon", "sen", "rep", "jan", "feb", "mar",
        "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    };

    /// <summary>
    /// Whether the period at <paramref name="dot"/> ends a sentence, given that <paramref name="after"/> is the index
    /// just past the period and any closing quotes.
    /// </summary>
    public static bool IsBoundaryAfterPeriod(string text, int dot, int after)
    {
        if (EndsWithNonTerminalWord(text, dot)) return false;

        // "etc. and" continues; "end. The" starts a new sentence.
        var n = after;
        while (n < text.Length && char.IsWhiteSpace(text[n])) n++;
        return n >= text.Length || !char.IsLower(text[n]);
    }

    /// <summary>True when the period at <paramref name="dot"/> belongs to an abbreviation or a single-letter initial.</summary>
    public static bool EndsWithNonTerminalWord(string text, int dot)
    {
        var w = dot;
        while (w > 0 && (char.IsLetter(text[w - 1]) || text[w - 1] == '.')) w--;
        var word = text[w..dot];
        return Abbreviations.Contains(word) || (word.Length == 1 && char.IsUpper(word[0]));
    }
}
