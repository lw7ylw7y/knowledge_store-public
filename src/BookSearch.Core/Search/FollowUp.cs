namespace BookSearch.Core.Search;

/// <summary>
/// Decides whether a question may depend on the conversation (FR-40): one that starts with a pronoun or a connector ("and his
/// sister?", "what about Elizabeth") or that has an unambiguous pronoun anywhere in it ("Why does it break down?"). Length plays
/// no part. It is only a gate, with no language model: when it says yes, <see cref="QuestionRewriter"/> asks the model to make
/// the question stand on its own, and only that rewritten question is searched and summarized. A question that stands on its own
/// but contains such a word costs one unnecessary model call and is returned unchanged. A follow-up with none of these words
/// ("why?") is not rewritten (the question box can be edited to say what it means).
/// </summary>
public static class FollowUp
{
    // Words that make a question a follow-up when it STARTS with one. Connectors and words that are also common articles or
    // conjunctions ("el", "y", "es", "sie") are only checked here: anywhere in a sentence they would flag nearly every question.
    private static readonly HashSet<string> Starters = new(StringComparer.Ordinal)
    {
        // English
        "and", "but", "also", "then", "so", "he", "she", "it", "they", "him", "her", "them", "his", "hers", "its", "their",
        "this", "that", "these", "those", "there",
        // Spanish
        "y", "pero", "tambien", "entonces", "el", "ella", "ellos", "ellas", "esto", "eso", "ese", "esa", "su", "sus",
        // French
        "et", "mais", "aussi", "alors", "il", "elle", "ils", "elles", "ce", "cela", "ca", "son", "sa", "ses",
        // German
        "und", "aber", "auch", "dann", "er", "sie", "es", "dies", "sein", "ihr",
    };

    // Pronouns that make a question a follow-up wherever they stand.
    private static readonly HashSet<string> Pronouns = new(StringComparer.Ordinal)
    {
        // English
        "he", "she", "it", "they", "him", "her", "them", "his", "hers", "its", "their", "this", "that", "these", "those",
        // Spanish
        "ella", "ellos", "ellas", "esto", "eso", "ese", "esa", "sus",
        // French
        "il", "elle", "ils", "elles", "cela", "ca", "ses",
        // German
        "er", "dies",
    };

    private static readonly HashSet<(string, string)> StarterPairs =
    [
        ("what", "about"), ("how", "about"), ("que", "tal"), ("y", "si"), ("et", "si"), ("und", "was"),
    ];

    /// <param name="previousQuestions">The earlier questions of the conversation, oldest first.</param>
    /// <param name="count">How many of the newest earlier questions the rewriter may see (Settings, 0 to 3); 0 turns follow-up handling off.</param>
    /// <returns>The earlier questions to give the rewriter, oldest first; null when the question stands on its own, there is no earlier question, or <paramref name="count"/> is 0.</returns>
    public static IReadOnlyList<string>? Detect(string question, IReadOnlyList<string> previousQuestions, int count = 1)
    {
        var earlier = previousQuestions.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).ToList();
        if (count <= 0 || earlier.Count == 0 || !LooksLikeFollowUp(question)) return null;
        return earlier.TakeLast(count).ToList();
    }

    public static bool LooksLikeFollowUp(string question)
    {
        var words = NameFolding.Words(NameFolding.Fold(question));
        if (words.Length == 0) return false;

        if (Starters.Contains(words[0])) return true;
        if (words.Length > 1 && StarterPairs.Contains((words[0], words[1]))) return true;
        return words.Any(Pronouns.Contains);
    }
}
