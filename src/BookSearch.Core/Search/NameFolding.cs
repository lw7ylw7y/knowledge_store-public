using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BookSearch.Core.Search;

/// <summary>
/// Turns a title, an author or part of a question into a comparable form: lower case, no accents, no punctuation, single
/// spaces ("Don Quíjote" and "don quijote" become the same). Works the same with and without ICU, because the accent table is
/// built in rather than relying on Unicode normalization (which does nothing on a machine without ICU).
/// </summary>
public static partial class NameFolding
{
    private static readonly Dictionary<char, string> Accents = BuildAccentTable();

    private static Dictionary<char, string> BuildAccentTable()
    {
        var table = new Dictionary<char, string>();
        void Add(string baseLetters, string accented)
        {
            foreach (var c in accented) table[c] = baseLetters;
        }
        Add("a", "àáâãäåāăą"); Add("c", "çćĉċč"); Add("d", "ďđ"); Add("e", "èéêëēĕėęě"); Add("g", "ĝğġģ");
        Add("h", "ĥħ"); Add("i", "ìíîïĩīĭįı"); Add("j", "ĵ"); Add("k", "ķ"); Add("l", "ĺļľŀł"); Add("n", "ñńņň");
        Add("o", "òóôõöøōŏő"); Add("r", "ŕŗř"); Add("s", "śŝşš"); Add("t", "ţťŧ"); Add("u", "ùúûüũūŭůűų");
        Add("w", "ŵ"); Add("y", "ýÿŷ"); Add("z", "źżž");
        table['ß'] = "ss"; table['æ'] = "ae"; table['œ'] = "oe"; table['ð'] = "d"; table['þ'] = "th";
        return table;
    }

    /// <summary>The comparable form of <paramref name="text"/>: words separated by single spaces.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lower = Possessive().Replace(text.ToLowerInvariant(), "");
        var decomposed = lower.Normalize(NormalizationForm.FormD);          // strips accents where ICU is available
        var result = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (Accents.TryGetValue(c, out var plain)) result.Append(plain);   // and the built-in table where it is not
            else if (char.IsLetterOrDigit(c)) result.Append(c);
            else result.Append(' ');
        }
        return Spaces().Replace(result.ToString(), " ").Trim();
    }

    /// <summary>The words of a folded string.</summary>
    public static string[] Words(string folded) =>
        folded.Length == 0 ? [] : folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"(?<=\p{L})['’]s\b")]
    private static partial Regex Possessive();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex Spaces();
}
