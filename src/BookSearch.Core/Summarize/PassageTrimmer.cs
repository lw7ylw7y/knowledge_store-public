using BookSearch.Core.Chunking;
using BookSearch.Core.Search;

namespace BookSearch.Core.Summarize;

/// <param name="Text">The kept sentences, with "…" where text was cut off.</param>
public sealed record TrimmedPassage(string Text, int Tokens, bool CutStart, bool CutEnd);

/// <summary>
/// Cuts a passage down to a window of whole sentences around its best match, so the prompt stays small enough for a
/// CPU-only model to read quickly (about 300 tokens per passage).
/// </summary>
public sealed class PassageTrimmer(ITokenizer tokenizer, ISentenceSplitter splitter)
{
    public const int DefaultMaxTokens = 300;

    /// <summary>
    /// The best match is the sentence containing the most query terms (the start of the passage when none match).
    /// The window grows one sentence at a time, following then preceding, while it stays within the token budget.
    /// </summary>
    public TrimmedPassage Trim(string passage, QueryTerms terms, string? language = null, int maxTokens = DefaultMaxTokens)
    {
        var spans = splitter.Split(passage, language);
        if (spans.Count == 0) return new TrimmedPassage(passage.Trim(), 0, false, false);

        var sentences = spans.Select(s => passage.Substring(s.Start, s.Length)).ToList();
        var counts = tokenizer.Count(sentences);
        var total = counts.Sum();
        if (total <= maxTokens) return new TrimmedPassage(Slice(passage, spans, 0, spans.Count - 1), total, false, false);

        var best = 0;
        var bestScore = 0;
        for (var i = 0; i < sentences.Count; i++)
        {
            var score = ExcerptBuilder.FindMatches(sentences[i], terms).Count;
            if (score > bestScore) { bestScore = score; best = i; }
        }

        int lo = best, hi = best, tokens = counts[best];
        var growForward = true;
        while (true)
        {
            var progressed = false;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var forward = growForward == (attempt == 0);
                var next = forward ? hi + 1 : lo - 1;
                if (next < 0 || next >= sentences.Count || tokens + counts[next] > maxTokens) continue;
                if (forward) hi = next; else lo = next;
                tokens += counts[next];
                progressed = true;
                growForward = !forward;       // alternate sides so the match stays near the middle
                break;
            }
            if (!progressed) break;
        }

        var cutStart = lo > 0;
        var cutEnd = hi < sentences.Count - 1;
        var text = (cutStart ? "… " : "") + Slice(passage, spans, lo, hi) + (cutEnd ? " …" : "");
        return new TrimmedPassage(text, tokens, cutStart, cutEnd);
    }

    private static string Slice(string passage, IReadOnlyList<(int Start, int Length)> spans, int lo, int hi) =>
        passage[spans[lo].Start..(spans[hi].Start + spans[hi].Length)];
}
