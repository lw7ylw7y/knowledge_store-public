namespace BookSearch.Parsing;

/// <summary>
/// Small dependency-free language guesser returning BCP-47 primary tags (or "und"). Non-Latin scripts are
/// identified by Unicode block; Latin-script languages by common function words.
/// </summary>
public static class LanguageDetector
{
    private static readonly Dictionary<string, HashSet<string>> Stopwords = new()
    {
        ["en"] = ["the", "and", "of", "to", "in", "was", "that", "it", "he", "she", "with", "for", "his", "her", "is"],
        ["es"] = ["el", "la", "de", "que", "y", "en", "los", "se", "del", "las", "por", "un", "con", "una", "su"],
        ["fr"] = ["le", "la", "les", "de", "des", "et", "est", "que", "un", "une", "du", "en", "dans", "qui", "pas"],
        ["de"] = ["der", "die", "und", "das", "ist", "nicht", "ein", "eine", "zu", "mit", "den", "von", "sie", "auf", "ich"],
        ["it"] = ["il", "di", "che", "la", "e", "un", "per", "non", "una", "del", "le", "con", "sono", "si", "come"],
        ["pt"] = ["o", "de", "que", "e", "do", "da", "em", "um", "para", "com", "uma", "os", "no", "se", "não"],
    };

    private static readonly char[] TrimChars = ".,;:!?\"'()[]«»“”‘’—-".ToCharArray();

    public static string Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "und";
        var sample = Sample(text);

        int kana = 0, han = 0, hangul = 0, arabic = 0, hebrew = 0, thai = 0, cyrillic = 0, latin = 0, letters = 0;
        foreach (var ch in sample)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            switch (ch)
            {
                case >= '぀' and <= 'ヿ': kana++; break;
                case >= '一' and <= '鿿': han++; break;
                case >= '가' and <= '힯': hangul++; break;
                case >= '؀' and <= 'ۿ': arabic++; break;
                case >= '֐' and <= '׿': hebrew++; break;
                case >= '฀' and <= '๿': thai++; break;
                case >= 'Ѐ' and <= 'ӿ': cyrillic++; break;
                default: latin++; break;
            }
        }
        if (letters == 0) return "und";

        // Japanese mixes kana with han; any meaningful kana share means Japanese.
        if (kana * 10 >= letters) return "ja";
        var scripts = new (string Tag, int Count)[]
            { ("zh", han), ("ko", hangul), ("ar", arabic), ("he", hebrew), ("th", thai), ("ru", cyrillic) };
        var top = scripts.MaxBy(s => s.Count);
        if (top.Count * 2 >= letters) return top.Tag;
        if (latin * 2 < letters) return "und";
        return DetectLatin(sample);
    }

    private static string DetectLatin(string sample)
    {
        var words = sample.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(TrimChars).ToLowerInvariant());
        var scores = Stopwords.ToDictionary(kv => kv.Key, _ => 0);
        foreach (var w in words)
            foreach (var (lang, set) in Stopwords)
                if (set.Contains(w)) scores[lang]++;
        var best = scores.MaxBy(kv => kv.Value);
        return best.Value == 0 ? "und" : best.Key;
    }

    /// <summary>Up to 20k characters from the middle, skipping front matter and license boilerplate.</summary>
    private static string Sample(string text)
    {
        const int size = 20_000;
        if (text.Length <= size) return text;
        return text.Substring((text.Length - size) / 2, size);
    }
}
