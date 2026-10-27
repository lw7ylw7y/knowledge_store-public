namespace BookSearch.Core.Chunking;

/// <summary>
/// Counts tokens the way the embedding model does. Production uses the embedding server's /tokenize endpoint
/// (ST-04); <see cref="ApproximateTokenizer"/> stands in until then and in the console tools.
/// </summary>
public interface ITokenizer
{
    /// <summary>Token counts for each text, in order. Batched so a server-backed tokenizer needs few round trips.</summary>
    IReadOnlyList<int> Count(IReadOnlyList<string> texts);
}

/// <summary>Model-free estimate: about one token per four characters, and one per CJK character.</summary>
public sealed class ApproximateTokenizer : ITokenizer
{
    public IReadOnlyList<int> Count(IReadOnlyList<string> texts)
    {
        var counts = new int[texts.Count];
        for (var i = 0; i < counts.Length; i++) counts[i] = CountOne(texts[i]);
        return counts;
    }

    public static int CountOne(string text)
    {
        int cjk = 0, other = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (CjkText.IsCjk(ch)) cjk++;
            else other++;
        }
        return Math.Max(1, cjk + (other + 3) / 4);
    }
}

internal static class CjkText
{
    /// <summary>Han, kana, Hangul, Thai and full-width forms: scripts written without spaces between words.</summary>
    public static bool IsCjk(char ch) =>
        ch is (>= '　' and <= 'ヿ') or (>= '㐀' and <= '鿿') or (>= '가' and <= '힯')
            or (>= '＀' and <= '￯') or (>= '฀' and <= '๿');
}
