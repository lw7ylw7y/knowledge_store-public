using BookSearch.Core.Chunking;
using BookSearch.Core.Data;
using BookSearch.Core.Reading;
using BookSearch.Core.Search;

namespace BookSearch.Core.Summarize;

/// <summary>Fetches the chunks around a hit, so the model can read what comes before and after it.</summary>
public interface IChunkSource
{
    /// <summary>The chunks of the book from <paramref name="fromIndex"/> to <paramref name="toIndex"/> in the same section, in reading order.</summary>
    Task<IReadOnlyList<Chunk>> GetRunAsync(long bookId, long? sectionId, int fromIndex, int toIndex, CancellationToken ct);
}

/// <summary>An <see cref="IChunkSource"/> over a library connection that one caller uses at a time (the console tool).</summary>
public sealed class ConnectionChunkSource(Microsoft.Data.Sqlite.SqliteConnection conn) : IChunkSource
{
    public Task<IReadOnlyList<Chunk>> GetRunAsync(long bookId, long? sectionId, int fromIndex, int toIndex, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Chunk>>(new ChunkRepository(conn).GetRun(bookId, sectionId, fromIndex, toIndex));
}

/// <summary>
/// The text the model reads for one result (ST-34): the result's own chunk, cut to a token budget around the question's words,
/// with a little text from before and after it so "he" and "that night" have something to point at.
/// </summary>
/// <param name="Label">The book's title and place, as the prompt names the excerpt.</param>
public sealed record SummaryContext(string Label, string Text, int Tokens);

/// <summary>Builds the <see cref="SummaryContext"/> of one search hit (a chunk, trimmed, with its neighbouring sentences).</summary>
public sealed class ContextBuilder(ITokenizer tokenizer, ISentenceSplitter splitter, SummaryOptions options, IChunkSource? chunks)
{
    public async Task<SummaryContext> BuildAsync(SearchHit hit, QueryTerms terms, CancellationToken ct = default)
    {
        var neighbors = options.NeighborTokens > 0 && chunks is not null ? 1 : 0;

        // The hit's own chunk is always there; the source adds the neighbours on either side.
        var byIndex = new SortedDictionary<int, Chunk>();
        byIndex[hit.ChunkIndex] = new Chunk { Id = hit.ChunkId, BookId = hit.BookId, SectionId = hit.SectionId, ChunkIndex = hit.ChunkIndex, Text = hit.Text };
        if (chunks is not null && neighbors > 0)
            foreach (var chunk in await chunks.GetRunAsync(hit.BookId, hit.SectionId, Math.Max(0, hit.ChunkIndex - neighbors), hit.ChunkIndex + neighbors, ct).ConfigureAwait(false))
                byIndex[chunk.ChunkIndex] = chunk;

        var merged = ChapterText.Merge(byIndex.Values.ToList());
        var ranges = merged.Ranges.ToDictionary(r => r.ChunkIndex);
        var text = merged.Text;
        var coreStart = ranges.TryGetValue(hit.ChunkIndex, out var own) ? own.Start : 0;
        var coreEnd = own?.End ?? text.Length;
        var core = text[coreStart..coreEnd];
        var before = neighbors > 0 && ranges.ContainsKey(hit.ChunkIndex - 1) ? text[..coreStart] : "";
        var after = neighbors > 0 && ranges.ContainsKey(hit.ChunkIndex + 1) ? text[coreEnd..] : "";

        // The core stays within a budget; when it has to be cut, the cut sides get no neighbour text (they already have a gap).
        var trimmed = new PassageTrimmer(tokenizer, splitter).Trim(core, terms, hit.Language, options.TokensPerPassage);
        if (trimmed.CutStart) before = "";
        if (trimmed.CutEnd) after = "";

        before = LastSentences(before, options.NeighborTokens, hit.Language);
        after = FirstSentences(after, options.NeighborTokens, hit.Language);
        var full = (before.Length > 0 ? "… " + before.TrimStart() + " " : "") + trimmed.Text + (after.Length > 0 ? " " + after.TrimEnd() + " …" : "");
        var tokens = tokenizer.Count([full]).Sum();
        return new SummaryContext(SummaryPrompt.Label(hit), full, tokens);
    }

    private string FirstSentences(string text, int maxTokens, string? language)
    {
        if (string.IsNullOrWhiteSpace(text) || maxTokens <= 0) return "";
        var spans = splitter.Split(text, language);
        if (spans.Count == 0) return "";
        var counts = tokenizer.Count(spans.Select(s => text.Substring(s.Start, s.Length)).ToList());
        var used = 0;
        var last = -1;
        for (var i = 0; i < spans.Count && used + counts[i] <= maxTokens; i++) { used += counts[i]; last = i; }
        return last < 0 ? "" : text[spans[0].Start..(spans[last].Start + spans[last].Length)];
    }

    private string LastSentences(string text, int maxTokens, string? language)
    {
        if (string.IsNullOrWhiteSpace(text) || maxTokens <= 0) return "";
        var spans = splitter.Split(text, language);
        if (spans.Count == 0) return "";
        var counts = tokenizer.Count(spans.Select(s => text.Substring(s.Start, s.Length)).ToList());
        var used = 0;
        var firstKept = spans.Count;
        for (var i = spans.Count - 1; i >= 0 && used + counts[i] <= maxTokens; i--) { used += counts[i]; firstKept = i; }
        return firstKept >= spans.Count ? "" : text[spans[firstKept].Start..(spans[^1].Start + spans[^1].Length)];
    }
}
