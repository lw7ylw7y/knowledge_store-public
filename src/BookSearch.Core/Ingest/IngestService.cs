using System.Diagnostics;
using System.Security.Cryptography;
using BookSearch.Core.Chunking;
using BookSearch.Core.Data;
using BookSearch.Parsing;
using Microsoft.Data.Sqlite;

namespace BookSearch.Core.Ingest;

public sealed record IngestOptions
{
    /// <summary>Chunks embedded and written per transaction (16-32 keeps the embedding server busy).</summary>
    public int BatchSize { get; init; } = 16;
    public ChunkOptions Chunking { get; init; } = new();
}

/// <summary>What <see cref="IngestService.Add"/> did with a file.</summary>
/// <param name="Duplicate">The same content is already in the library (or was rejected before); nothing new was queued.</param>
public sealed record AddResult(long BookId, bool Duplicate, string Status, string? Reason);

public sealed record IngestProgress(long BookId, string Stage, int ChunksDone, int ChunksTotal);

/// <summary>Time spent per stage; contains counts and durations only, never book text.</summary>
public sealed record IngestTimings(TimeSpan Extract, TimeSpan Chunk, TimeSpan Embed, TimeSpan Write, int Chunks);

public sealed record IngestResult(long BookId, string Status, string? Reason, IngestTimings Timings);

/// <summary>
/// Hash, queue, extract, chunk, embed and store books. Every batch of chunks is written in one transaction together
/// with the job cursor, so an interrupted ingest resumes at the next batch without duplicate or partial rows.
/// </summary>
public sealed class IngestService
{
    private readonly SqliteConnection _conn;
    private readonly IEmbedder _embedder;
    private readonly ITokenizer _tokenizer;
    private readonly ISentenceSplitter _splitter;
    private readonly IngestOptions _options;
    private readonly BookRepository _books;
    private readonly SectionRepository _sections;
    private readonly IngestJobRepository _jobs;
    private readonly Func<CancellationToken, Task>? _beforeBatch;

    public IngestService(SqliteConnection conn, IEmbedder embedder, ITokenizer tokenizer,
        ISentenceSplitter? splitter = null, IngestOptions? options = null,
        Func<CancellationToken, Task>? beforeBatch = null)
    {
        _beforeBatch = beforeBatch;
        _conn = conn;
        _embedder = embedder;
        _tokenizer = tokenizer;
        _splitter = splitter ?? SentenceSplitters.CreateDefault();
        _options = options ?? new IngestOptions();
        _books = new BookRepository(conn);
        _sections = new SectionRepository(conn);
        _jobs = new IngestJobRepository(conn);
    }

    // ---- accept -----------------------------------------------------------------------------------------

    /// <summary>
    /// Hashes the file and queues it. Returns immediately; call <see cref="IngestAsync"/> (or the worker) to index.
    /// A file whose content is already known is reported as a duplicate; a previously failed book is re-queued.
    /// </summary>
    public async Task<AddResult> AddAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Book file not found.", path);

        var hash = await HashAsync(info.FullName, ct).ConfigureAwait(false);
        if (_books.GetByHash(hash) is { } existing)
        {
            if (existing.Status != BookStatus.Failed)
                return new AddResult(existing.Id, true, existing.Status, existing.StatusReason);

            // Retry: keep the book and its job cursor, and put it back in the queue.
            _books.SetStatus(existing.Id, BookStatus.Queued);
            if (_jobs.Get(existing.Id) is null) _jobs.Insert(existing.Id);
            else _jobs.ResetAttempts(existing.Id);
            return new AddResult(existing.Id, false, BookStatus.Queued, null);
        }

        var format = info.Extension.TrimStart('.').ToLowerInvariant();
        var id = Database.InTransaction(_conn, tx =>
        {
            var bookId = _books.Insert(new Book
            {
                FilePath = info.FullName,
                FileHash = hash,
                Title = Path.GetFileNameWithoutExtension(info.Name),
                Format = format,
                Status = BookStatus.Queued,
            }, tx);
            _jobs.Insert(bookId, IngestStage.Extract, tx);
            return bookId;
        });
        return new AddResult(id, false, BookStatus.Queued, null);
    }

    // ---- ingest -----------------------------------------------------------------------------------------

    /// <summary>Indexes one queued book, or resumes it from its saved cursor. Exceptions leave the job resumable.</summary>
    public async Task<IngestResult> IngestAsync(long bookId, IProgress<IngestProgress>? progress = null,
        CancellationToken ct = default)
    {
        var book = _books.GetById(bookId) ?? throw new ArgumentException($"No book with id {bookId}.", nameof(bookId));
        var job = _jobs.Get(bookId);
        if (book.Status is BookStatus.Ready or BookStatus.Rejected || job is null or { Stage: IngestStage.Done })
            return new IngestResult(bookId, book.Status, book.StatusReason, new IngestTimings(default, default, default, default, 0));

        var timer = Stopwatch.StartNew();
        TimeSpan extract = default, chunk = default, embed = default, write = default;
        _books.SetStatus(bookId, BookStatus.Indexing);

        // A moved or edited file can't be resumed: the cursor refers to chunks of the original text.
        if (!File.Exists(book.FilePath)) return Fail(bookId, BookStatus.Failed, "The file is missing.");
        if (await HashAsync(book.FilePath, ct).ConfigureAwait(false) != book.FileHash)
            return Fail(bookId, BookStatus.Failed, "The file changed since it was added.");

        // Extract
        _jobs.SetStage(bookId, job.Stage, progress: 0);
        progress?.Report(new IngestProgress(bookId, IngestStage.Extract, 0, 0));
        var extraction = await Task.Run(() => Extractor.Extract(book.FilePath), ct).ConfigureAwait(false);
        extract = timer.Elapsed;

        if (extraction.Rejection is { } rejection)
            return Fail(bookId, rejection.Code == RejectionCode.Corrupt ? BookStatus.Failed : BookStatus.Rejected, rejection.Reason);
        var extracted = extraction.Book!;

        var sectionIds = job.Stage == IngestStage.Extract
            ? StoreExtracted(bookId, extracted)
            : _sections.ListByBook(bookId).ToDictionary(s => s.Order, s => s.Id);

        // Chunk (deterministic, so a resumed job produces the same chunks and can skip the finished ones)
        var chunkStart = timer.Elapsed;
        var chunks = await Task.Run(
            () => new Chunker(_tokenizer, _splitter, _options.Chunking).Chunk(extracted), ct).ConfigureAwait(false);
        chunk = timer.Elapsed - chunkStart;
        if (chunks.Count == 0) return Fail(bookId, BookStatus.Rejected, "No text could be extracted from this file.");

        // Embed and write, one transaction per batch
        var cjk = IsCjkLanguage(extracted.Language);
        for (var next = job.NextChunkIndex; next < chunks.Count; next += _options.BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            if (_beforeBatch is not null) await _beforeBatch(ct).ConfigureAwait(false);   // the worker pauses here
            var batch = chunks.Skip(next).Take(_options.BatchSize).ToList();

            var embedStart = timer.Elapsed;
            var vectors = await _embedder.EmbedDocumentsAsync(batch.Select(c => c.Text).ToList(), ct).ConfigureAwait(false);
            embed += timer.Elapsed - embedStart;
            if (vectors.Count != batch.Count)
                throw new InvalidOperationException($"Embedder returned {vectors.Count} vectors for {batch.Count} chunks.");

            var writeStart = timer.Elapsed;
            var writes = batch.Select((c, i) => new ChunkWrite(new Data.Chunk
            {
                BookId = bookId,
                SectionId = sectionIds.TryGetValue(c.SectionOrder, out var sid) ? sid : null,
                ChunkIndex = c.Index,
                Text = c.Text,
                TokenCount = c.TokenCount,
                PageStart = c.PageStart,
                PageEnd = c.PageEnd,
                Location = c.Location,
            }, vectors[i], cjk)).ToList();
            var done = next + batch.Count;
            _jobs.WriteBatch(bookId, writes, done, (double)done / chunks.Count);
            write += timer.Elapsed - writeStart;

            progress?.Report(new IngestProgress(bookId, IngestStage.Embed, done, chunks.Count));
        }

        Database.InTransaction(_conn, tx =>
        {
            _books.SetStatus(bookId, BookStatus.Ready, null, tx);
            _jobs.SetStage(bookId, IngestStage.Done, progress: 1, tx: tx);
        });
        return new IngestResult(bookId, BookStatus.Ready, null, new IngestTimings(extract, chunk, embed, write, chunks.Count));
    }

    /// <summary>Continues every job that isn't done (queued, interrupted or failed), oldest first.</summary>
    public async Task<IReadOnlyList<IngestResult>> ResumeAllAsync(IProgress<IngestProgress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<IngestResult>();
        foreach (var job in _jobs.ListUnfinished())
            results.Add(await IngestAsync(job.BookId, progress, ct).ConfigureAwait(false));
        return results;
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    /// <summary>Saves metadata and sections and advances the job to the embed stage, atomically.</summary>
    private Dictionary<int, long> StoreExtracted(long bookId, ExtractedBook extracted)
    {
        var ids = new Dictionary<int, long>();
        Database.InTransaction(_conn, tx =>
        {
            _books.SetExtracted(bookId, extracted.Metadata.Title, extracted.Metadata.Author, extracted.Language,
                _embedder.ModelId, tx);
            foreach (var s in extracted.Sections.OrderBy(s => s.Order))
                ids[s.Order] = _sections.Insert(new Section
                {
                    BookId = bookId,
                    ParentId = s.ParentOrder is { } p && ids.TryGetValue(p, out var parentId) ? parentId : null,
                    Title = s.Title,
                    Order = s.Order,
                }, tx);
            _jobs.SetStage(bookId, IngestStage.Embed, tx: tx);
        });
        return ids;
    }

    private IngestResult Fail(long bookId, string status, string reason)
    {
        // A rejected book is finished; a failed one stays in the queue at its current stage so it can be retried.
        var stage = status == BookStatus.Rejected ? IngestStage.Done : _jobs.Get(bookId)!.Stage;
        Database.InTransaction(_conn, tx =>
        {
            _books.SetStatus(bookId, status, reason, tx);
            _jobs.SetStage(bookId, stage, error: reason, tx: tx);
        });
        return new IngestResult(bookId, status, reason, new IngestTimings(default, default, default, default, 0));
    }

    private static bool IsCjkLanguage(string? language) => language is "zh" or "ja" or "ko" or "th";

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}
