using System.Globalization;

namespace BookSearch.Core.Search;

/// <summary>
/// The searchable parts of a question: words for the word index and 3-character grams for the trigram index
/// (used for Chinese, Japanese, Korean and Thai, which have no spaces between words).
/// </summary>
public sealed record QueryTerms(IReadOnlyList<string> Words, IReadOnlyList<string> Trigrams)
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "about", "am", "an", "and", "any", "are", "as", "at", "be", "been", "but", "by", "can", "could", "did",
        "do", "does", "for", "from", "had", "has", "have", "he", "her", "him", "his", "how", "i", "if", "in", "is",
        "it", "its", "me", "my", "no", "not", "of", "on", "or", "our", "she", "should", "so", "than", "that", "the",
        "their", "them", "then", "there", "these", "they", "this", "those", "to", "was", "we", "were", "what", "when",
        "where", "which", "who", "whom", "why", "will", "with", "would", "you", "your",
    };

    public bool HasWords => Words.Count > 0;
    public bool HasTrigrams => Trigrams.Count > 0;

    /// <summary>FTS5 MATCH expression over the word index: quoted terms joined with OR (BM25 favours rarer terms).</summary>
    public string? WordMatchExpression => HasWords ? string.Join(" OR ", Words.Select(w => $"\"{w}\"")) : null;

    public string? TrigramMatchExpression => HasTrigrams ? string.Join(" OR ", Trigrams.Select(t => $"\"{t}\"")) : null;

    /// <summary>The words and grams of several queries together (the question and its translations), each listed once.</summary>
    public static QueryTerms Union(IReadOnlyList<QueryTerms> all) =>
        all.Count == 1 ? all[0] : new QueryTerms(all.SelectMany(t => t.Words).Distinct().ToList(), all.SelectMany(t => t.Trigrams).Distinct().ToList());

    public static QueryTerms Extract(string question, int maxWords = 32, int maxTrigrams = 40)
    {
        var words = new List<string>();
        var trigrams = new List<string>();
        var word = new System.Text.StringBuilder();
        var run = new System.Text.StringBuilder();

        void EndWord()
        {
            if (word.Length == 0) return;
            var w = word.ToString().ToLowerInvariant();
            word.Clear();
            if ((w.Length > 1 || char.IsDigit(w[0])) && !Stopwords.Contains(w) && !words.Contains(w) && words.Count < maxWords)
                words.Add(w);
        }

        void EndRun()
        {
            var text = run.ToString();
            run.Clear();
            for (var i = 0; i + 3 <= text.Length && trigrams.Count < maxTrigrams; i++)
            {
                var gram = text.Substring(i, 3);
                if (!trigrams.Contains(gram)) trigrams.Add(gram);
            }
        }

        foreach (var ch in question)
        {
            if (IsRunChar(ch))
            {
                EndWord();
                run.Append(ch);
            }
            else if (char.IsLetterOrDigit(ch))
            {
                EndRun();
                word.Append(ch);
            }
            else
            {
                EndWord();
                EndRun();
            }
        }
        EndWord();
        EndRun();
        return new QueryTerms(words, trigrams);
    }

    /// <summary>Characters of scripts written without spaces, including Thai vowel and tone marks.</summary>
    private static bool IsRunChar(char ch) =>
        Chunking.CjkText.IsCjk(ch) && (char.IsLetter(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) is
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark);
}
