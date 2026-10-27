using BookSearch.Parsing;

namespace BookSearch.Core.Search;

/// <summary>
/// Which languages a question is also searched in (DCR-11). When the books being searched are in two or more languages the question
/// is asked for in all of them, the most books first, at most <see cref="MaxTargets"/>, with <b>no guess at the question's own
/// language</b>: the translator leaves a question already in a language unchanged, and <see cref="QueryTranslator"/> drops that copy.
/// (The detector can't tell the language of a short question or a name, and was the reason a search sometimes was not translated.)
/// When the books are in one language L, only a question that is clearly in another language needs translating, into L; there the
/// detector is still used, and a miss only means no translation, as before DCR-11. No books in a named language: nothing.
/// </summary>
public static class TranslationTargets
{
    public const int MaxTargets = 3;

    /// <summary>The languages the translator knows by name; a book in any other language is searched with the question as typed.</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["zh"] = "Chinese", ["ja"] = "Japanese", ["ko"] = "Korean", ["ar"] = "Arabic", ["he"] = "Hebrew",
        ["th"] = "Thai", ["ru"] = "Russian", ["es"] = "Spanish", ["fr"] = "French", ["de"] = "German", ["it"] = "Italian",
        ["pt"] = "Portuguese",
    };

    /// <param name="libraryLanguages">The language codes of the books in scope, the language with the most books first (duplicates, "und" and unnamed languages are ignored).</param>
    /// <returns>Language codes, lower case, each once.</returns>
    public static IReadOnlyList<string> Choose(string question, IReadOnlyList<string?> libraryLanguages)
    {
        var known = libraryLanguages.Where(l => l is not null && Names.ContainsKey(l)).Select(l => l!.ToLowerInvariant()).Distinct().ToList();
        if (known.Count >= 2) return known.Take(MaxTargets).ToList();
        if (known.Count == 0) return [];
        var own = LanguageDetector.Detect(question);
        return Names.ContainsKey(own) && !string.Equals(own, known[0], StringComparison.OrdinalIgnoreCase) ? known : [];
    }

    /// <summary>The English name of a language code, as the translator is asked for it.</summary>
    public static string NameOf(string code) => Names.TryGetValue(code, out var name) ? name : code;
}
