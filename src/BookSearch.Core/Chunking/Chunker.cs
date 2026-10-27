using System.Text.RegularExpressions;
using BookSearch.Parsing;

namespace BookSearch.Core.Chunking;

public sealed record ChunkOptions
{
    /// <summary>Chunks are filled up to about this many tokens.</summary>
    public int TargetTokens { get; init; } = 500;
    /// <summary>Hard ceiling when topping up a chunk that would otherwise be under-filled.</summary>
    public int MaxTokens { get; init; } = 550;
    /// <summary>Tokens repeated at the start of the next chunk (15% of the target by default).</summary>
    public int OverlapTokens { get; init; } = 75;
    /// <summary>Overlap may exceed <see cref="OverlapTokens"/> by this much to end on a sentence boundary.</summary>
    public int OverlapSlackTokens { get; init; } = 15;
}

/// <param name="Index">Position in the book, from 0; this is <c>chunk.chunk_index</c>.</param>
/// <param name="SectionOrder">The <see cref="ExtractedSection.Order"/> the chunk sits in.</param>
/// <param name="TokenCount">Sum of the sentence token counts (the embedding model's own count may differ by a token or two).</param>
public sealed record BookChunk(
    int Index, int SectionOrder, string Text, int TokenCount, int? PageStart, int? PageEnd, string? Location);

/// <summary>
/// Splits an extracted book into overlapping chunks of whole sentences. Chunks never cross a section
/// boundary, carry the page range or location of their text, and are identical for identical input
/// (so an interrupted ingest can re-chunk and resume at <c>next_chunk_index</c>).
/// </summary>
public sealed partial class Chunker(ITokenizer tokenizer, ISentenceSplitter splitter, ChunkOptions? options = null)
{
    private readonly ChunkOptions _options = options ?? new ChunkOptions();

    private sealed record Sentence(
        string Text, int Tokens, int Block, string SepBefore, int? Page, string? Location);

    public IReadOnlyList<BookChunk> Chunk(ExtractedBook book)
    {
        var chunks = new List<BookChunk>();
        var language = book.Language;
        foreach (var section in book.Sections.OrderBy(s => s.Order))
        {
            var blocks = book.Blocks.Where(b => b.SectionOrder == section.Order).ToList();
            if (blocks.Count == 0) continue;
            Pack(Sentences(blocks, language), section.Order, chunks);
        }
        return chunks;
    }

    // ---- sentences --------------------------------------------------------------------------------------

    private List<Sentence> Sentences(List<TextBlock> blocks, string? language)
    {
        var raw = new List<(string Text, int Start, int Block)>();
        for (var b = 0; b < blocks.Count; b++)
            foreach (var (start, length) in splitter.Split(blocks[b].Text, language))
                raw.Add((blocks[b].Text.Substring(start, length), start, b));

        var counts = tokenizer.Count(raw.Select(r => r.Text).ToList());
        var result = new List<Sentence>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var (text, start, block) = raw[i];
            var sep = i > 0 && raw[i - 1].Block == block && !JoinsWithoutSpace(raw[i - 1].Text, text) ? " " : "";
            var sentence = new Sentence(text, counts[i], block, sep, blocks[block].Page,
                Shift(blocks[block].Location, start));
            if (sentence.Tokens > _options.TargetTokens) result.AddRange(SplitOversize(sentence));
            else result.Add(sentence);
        }
        return result;
    }

    /// <summary>A sentence longer than a whole chunk is cut into pieces of at most the target size.</summary>
    private List<Sentence> SplitOversize(Sentence sentence)
    {
        var words = Words().Matches(sentence.Text).Select(m => (m.Index, m.Length)).ToList();
        var wordCounts = words.Count > 1 ? tokenizer.Count(words.Select(w => sentence.Text.Substring(w.Index, w.Length)).ToList()) : [];
        var spaced = words.Count > 1 && wordCounts.All(c => c <= _options.TargetTokens);

        // No spaces (or a single enormous word): cut by character instead.
        var units = spaced
            ? words.Select((w, i) => (w.Index, w.Length, Tokens: wordCounts[i])).ToList()
            : CharUnits(sentence.Text);

        var pieces = new List<Sentence>();
        int pieceStart = -1, pieceEnd = 0, pieceTokens = 0;
        void Flush()
        {
            if (pieceStart < 0) return;
            var sep = pieces.Count == 0 ? sentence.SepBefore : spaced ? " " : "";
            pieces.Add(sentence with
            {
                Text = sentence.Text[pieceStart..pieceEnd],
                Tokens = pieceTokens,
                SepBefore = sep,
                Location = Shift(sentence.Location, pieceStart),
            });
            pieceStart = -1;
            pieceTokens = 0;
        }

        foreach (var (index, length, tokens) in units)
        {
            if (pieceStart >= 0 && pieceTokens + tokens > _options.TargetTokens) Flush();
            if (pieceStart < 0) pieceStart = index;
            pieceEnd = index + length;
            pieceTokens += tokens;
        }
        Flush();
        return pieces;
    }

    private List<(int Index, int Length, int Tokens)> CharUnits(string text)
    {
        var elements = new List<(int, int)>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) elements.Add((e.ElementIndex, ((string)e.Current).Length));
        var counts = tokenizer.Count(elements.Select(x => text.Substring(x.Item1, x.Item2)).ToList());
        return elements.Select((x, i) => (x.Item1, x.Item2, counts[i])).ToList();
    }

    // ---- packing ----------------------------------------------------------------------------------------

    private void Pack(List<Sentence> sentences, int sectionOrder, List<BookChunk> output)
    {
        var minFill = _options.TargetTokens * 4 / 5;
        var carried = new List<Sentence>(); // overlap from the previous chunk
        var next = 0;

        while (next < sentences.Count)
        {
            var chunk = new List<Sentence>(carried);
            var total = chunk.Sum(s => s.Tokens);
            var added = 0;

            while (next < sentences.Count)
            {
                var s = sentences[next];
                var fits = total + s.Tokens <= _options.TargetTokens
                           || (total < minFill && total + s.Tokens <= _options.MaxTokens);
                if (!fits && added > 0) break;
                chunk.Add(s);
                total += s.Tokens;
                added++;
                next++;
            }

            output.Add(Build(chunk, sectionOrder, output.Count));
            carried = next < sentences.Count ? Overlap(chunk, sentences[next]) : [];
        }
    }

    /// <summary>
    /// The trailing whole sentences of a chunk to repeat in the next one: the group closest to the overlap target,
    /// never the whole chunk, and small enough that the next sentence still fits.
    /// </summary>
    private List<Sentence> Overlap(List<Sentence> chunk, Sentence upcoming)
    {
        var limit = _options.OverlapTokens + _options.OverlapSlackTokens;
        var best = 0;
        var bestDistance = int.MaxValue;
        var tokens = 0;
        for (var k = 1; k < chunk.Count; k++)
        {
            tokens += chunk[^k].Tokens;
            if (tokens > limit || tokens + upcoming.Tokens > _options.MaxTokens) break;
            var distance = Math.Abs(tokens - _options.OverlapTokens);
            if (distance < bestDistance) { best = k; bestDistance = distance; }
        }
        return best == 0 ? [] : chunk.GetRange(chunk.Count - best, best);
    }

    private static BookChunk Build(List<Sentence> sentences, int sectionOrder, int index)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < sentences.Count; i++)
        {
            if (i > 0) text.Append(sentences[i].Block != sentences[i - 1].Block ? "\n\n" : sentences[i].SepBefore);
            text.Append(sentences[i].Text);
        }
        var pages = sentences.Where(s => s.Page is not null).Select(s => s.Page!.Value).ToList();
        return new BookChunk(index, sectionOrder, text.ToString(), sentences.Sum(s => s.Tokens),
            pages.Count > 0 ? pages.Min() : null, pages.Count > 0 ? pages.Max() : null, sentences[0].Location);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    /// <summary>Moves a "path#offset" location forward by <paramref name="by"/> characters.</summary>
    internal static string? Shift(string? location, int by)
    {
        if (location is null) return null;
        var hash = location.LastIndexOf('#');
        if (hash < 0 || !int.TryParse(location.AsSpan(hash + 1), out var offset)) return location;
        return $"{location[..hash]}#{offset + by}";
    }

    private static bool JoinsWithoutSpace(string previous, string next) =>
        CjkText.IsCjk(previous[^1]) || CjkText.IsCjk(next[0]);

    [GeneratedRegex(@"\S+")]
    private static partial Regex Words();
}
