using BookSearch.Core.Chunking;
using BookSearch.Core.Data;
using BookSearch.Core.Ingest;
using Microsoft.Data.Sqlite;

namespace BookSearch.Tests;

/// <summary>Deterministic vectors from the text, and a hook that runs before each embedding call.</summary>
internal sealed class FakeEmbedder : IEmbedder
{
    public string ModelId => "fake-embedder";
    public int Calls { get; private set; }
    public Action<int>? BeforeCall { get; set; }

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        Calls++;
        BeforeCall?.Invoke(Calls);
        IReadOnlyList<float[]> vectors = texts.Select(t =>
        {
            var v = new float[Database.EmbeddingDimension];
            var h = t.GetHashCode();
            v[0] = 1;
            v[1 + (Math.Abs(h) % 1000)] = 1;
            return v;
        }).ToList();
        return Task.FromResult(vectors);
    }
}

public class IngestTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeEmbedder _embedder = new();
    private readonly List<string> _tempFiles = [];

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private IngestService Service(FakeEmbedder? embedder = null, int batchSize = 16) =>
        new(_db.Conn, embedder ?? _embedder, new WordCountTokenizer(), new PunctuationSentenceSplitter(),
            new IngestOptions { BatchSize = batchSize });

    private string TempCopy(string fixture, string? newName = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ingest-{Guid.NewGuid():N}-{newName ?? fixture}");
        File.Copy(Fixture(fixture), path);
        _tempFiles.Add(path);
        return path;
    }

    private long CountWhere(string table, string where) => _db.Count($"{table} WHERE {where}");

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in _tempFiles) File.Delete(f);
    }

    // ---- accept and duplicates ----------------------------------------------------------------------------

    [Fact]
    public async Task Ingest_SameFileTwice_SkipsDuplicate()
    {
        var service = Service();
        var first = await service.AddAsync(Fixture("notes.md"));
        await service.IngestAsync(first.BookId);
        var chunksAfterFirst = _db.Chunks.CountChunks();

        var second = await service.AddAsync(TempCopy("notes.md", "same-content-other-name.md"));

        Assert.True(second.Duplicate);
        Assert.Equal(first.BookId, second.BookId);
        Assert.Equal(BookStatus.Ready, second.Status);
        Assert.Equal(1, _db.Count("book"));
        Assert.Equal(chunksAfterFirst, _db.Chunks.CountChunks());
    }

    [Fact]
    public async Task Add_QueuesBookAndJobWithoutIndexing()
    {
        var added = await Service().AddAsync(Fixture("plain.txt"));

        var book = _db.Books.GetById(added.BookId)!;
        Assert.False(added.Duplicate);
        Assert.Equal(BookStatus.Queued, book.Status);
        Assert.Equal("txt", book.Format);
        Assert.Equal(IngestStage.Extract, _db.Jobs.Get(added.BookId)!.Stage);
        Assert.Equal(0, _db.Chunks.CountChunks());
        Assert.Equal(0, _embedder.Calls);
    }

    [Fact]
    public async Task Add_MissingFile_Throws() =>
        await Assert.ThrowsAsync<FileNotFoundException>(() => Service().AddAsync("/no/such/book.epub"));

    // ---- batches and resume -------------------------------------------------------------------------------

    [Fact]
    public async Task Ingest_Batch_WritesChunkFtsAndVecTogether()
    {
        var embedder = new FakeEmbedder();
        var seen = new List<(long Chunks, long Fts, long Vectors)>();
        embedder.BeforeCall = _ => seen.Add((_db.Chunks.CountChunks(), _db.Chunks.CountFts(), _db.Chunks.CountVectors()));
        var service = Service(embedder, batchSize: 16);

        var added = await service.AddAsync(Fixture("alice.epub"));
        var result = await service.IngestAsync(added.BookId);

        Assert.Equal(BookStatus.Ready, result.Status);
        Assert.True(embedder.Calls >= 4);
        // Before every call the three indexes agree and hold exactly the batches written so far.
        for (var i = 0; i < seen.Count; i++)
        {
            Assert.Equal(seen[i].Chunks, seen[i].Fts);
            Assert.Equal(seen[i].Chunks, seen[i].Vectors);
            Assert.Equal(i * 16, seen[i].Chunks);
        }
        Assert.Equal(result.Timings.Chunks, _db.Chunks.CountChunks());
        Assert.Equal(_db.Chunks.CountChunks(), _db.Chunks.CountVectors());
    }

    [Fact]
    public async Task Ingest_KillAfterBatch3_ResumesAtBatch4()
    {
        var crashing = new FakeEmbedder { BeforeCall = n => { if (n == 4) throw new InvalidOperationException("simulated crash"); } };
        var added = await Service(crashing).AddAsync(Fixture("alice.epub"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(crashing).IngestAsync(added.BookId));

        // Three batches are safely stored, the cursor says so, and nothing is half written.
        Assert.Equal(48, _db.Chunks.CountChunks());
        Assert.Equal(48, _db.Jobs.Get(added.BookId)!.NextChunkIndex);
        Assert.Equal(BookStatus.Indexing, _db.Books.GetById(added.BookId)!.Status);
        Assert.Equal(48, _db.Chunks.CountVectors());

        var resumed = new FakeEmbedder();
        var results = await Service(resumed).ResumeAllAsync();

        var result = Assert.Single(results);
        Assert.Equal(BookStatus.Ready, result.Status);
        var total = (int)_db.Chunks.CountChunks();
        Assert.True(total > 48);
        Assert.Equal((total - 48 + 15) / 16, resumed.Calls);   // only the remaining batches were embedded
        Assert.Equal(total, _db.Chunks.CountVectors());
        Assert.Equal(total, _db.Chunks.CountFts());
        Assert.Equal(0, CountDuplicateChunkIndexes());
        Assert.Equal(Enumerable.Range(0, total).Select(i => (long)i), ChunkIndexes(added.BookId));
        Assert.Equal(IngestStage.Done, _db.Jobs.Get(added.BookId)!.Stage);
        Assert.Empty(_db.Jobs.ListUnfinished());
    }

    [Fact]
    public async Task Resume_ProducesSameChunksAsAnUninterruptedRun()
    {
        var clean = Service();
        var a = await clean.AddAsync(Fixture("alice.epub"));
        await clean.IngestAsync(a.BookId);
        var expected = ChunkTexts(a.BookId);

        using var other = new TestDb();
        var crashing = new FakeEmbedder { BeforeCall = n => { if (n == 3) throw new InvalidOperationException("crash"); } };
        var service = new IngestService(other.Conn, crashing, new WordCountTokenizer(), new PunctuationSentenceSplitter());
        var b = await service.AddAsync(Fixture("alice.epub"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.IngestAsync(b.BookId));
        await new IngestService(other.Conn, new FakeEmbedder(), new WordCountTokenizer(), new PunctuationSentenceSplitter())
            .ResumeAllAsync();

        Assert.Equal(expected, ChunkTexts(b.BookId, other.Conn));
    }

    private long CountDuplicateChunkIndexes() => (long)new SqliteCommand(
        "SELECT COUNT(*) FROM (SELECT book_id, chunk_index FROM chunk GROUP BY 1, 2 HAVING COUNT(*) > 1)", _db.Conn).ExecuteScalar()!;

    private List<long> ChunkIndexes(long bookId)
    {
        using var cmd = new SqliteCommand($"SELECT chunk_index FROM chunk WHERE book_id = {bookId} ORDER BY chunk_index", _db.Conn);
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    private List<string> ChunkTexts(long bookId, SqliteConnection? conn = null)
    {
        using var cmd = new SqliteCommand($"SELECT text FROM chunk WHERE book_id = {bookId} ORDER BY chunk_index", conn ?? _db.Conn);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    // ---- rejection ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("scanned.pdf", "scanned")]
    [InlineData("drm.epub", "DRM")]
    [InlineData("locked.pdf", "password")]
    public async Task Ingest_RejectedBook_HasReasonAndNoChunks(string fixture, string reasonContains)
    {
        var service = Service();
        var added = await service.AddAsync(Fixture(fixture));

        var result = await service.IngestAsync(added.BookId);

        var book = _db.Books.GetById(added.BookId)!;
        Assert.Equal(BookStatus.Rejected, result.Status);
        Assert.Equal(BookStatus.Rejected, book.Status);
        Assert.Contains(reasonContains, book.StatusReason);
        Assert.Equal(0, _db.Chunks.CountChunks());
        Assert.Equal(0, _embedder.Calls);
        Assert.Empty(_db.Jobs.ListUnfinished());          // finished: nothing to resume

        var again = await service.AddAsync(Fixture(fixture));
        Assert.True(again.Duplicate);                      // re-adding doesn't re-run it
        Assert.Equal(BookStatus.Rejected, again.Status);
    }

    [Fact]
    public async Task Ingest_CorruptFile_FailsAndCanBeRetried()
    {
        var path = Path.Combine(Path.GetTempPath(), $"junk-{Guid.NewGuid():N}.epub");
        File.WriteAllBytes(path, new byte[1024]);
        _tempFiles.Add(path);
        var service = Service();
        var added = await service.AddAsync(path);

        var result = await service.IngestAsync(added.BookId);

        Assert.Equal(BookStatus.Failed, result.Status);
        Assert.Single(_db.Jobs.ListUnfinished());          // stays queued for a retry
        var retry = await service.AddAsync(path);
        Assert.False(retry.Duplicate);
        Assert.Equal(BookStatus.Queued, retry.Status);
    }

    [Fact]
    public async Task Ingest_FileChangedAfterQueueing_Fails()
    {
        var path = TempCopy("plain.txt");
        var service = Service();
        var added = await service.AddAsync(path);
        File.AppendAllText(path, "\nEdited after it was queued.");

        var result = await service.IngestAsync(added.BookId);

        Assert.Equal(BookStatus.Failed, result.Status);
        Assert.Contains("changed", result.Reason);
        Assert.Equal(0, _db.Chunks.CountChunks());
    }

    // ---- CJK trigram index --------------------------------------------------------------------------------

    [Fact]
    public async Task Ingest_CjkBook_WritesTrigramRows()
    {
        var service = Service();
        var added = await service.AddAsync(Fixture("kokoro.epub"));

        await service.IngestAsync(added.BookId);

        Assert.Equal("ja", _db.Books.GetById(added.BookId)!.Language);
        Assert.True(_db.Chunks.CountChunks() > 0);
        Assert.Equal(_db.Chunks.CountChunks(), _db.Chunks.CountFtsCjk());
        Assert.NotEmpty(_db.Chunks.FtsSearch("先生と", 5, cjk: true));
    }

    [Fact]
    public async Task Ingest_EnglishBook_SkipsTrigramRows()
    {
        var service = Service();
        var added = await service.AddAsync(Fixture("notes.md"));

        await service.IngestAsync(added.BookId);

        Assert.True(_db.Chunks.CountChunks() > 0);
        Assert.Equal(0, _db.Chunks.CountFtsCjk());
    }

    // ---- remove -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Remove_Book_PurgesEverything()
    {
        var service = Service();
        var keep = await service.AddAsync(Fixture("notes.md"));
        var gone = await service.AddAsync(Fixture("alice.epub"));
        await service.IngestAsync(keep.BookId);
        await service.IngestAsync(gone.BookId);
        var keptChunks = _db.Chunks.CountChunks(keep.BookId);

        _db.Books.Delete(gone.BookId);

        Assert.Equal(0, _db.Chunks.CountChunks(gone.BookId));
        Assert.Equal(0, _db.Chunks.CountVectors(gone.BookId));
        Assert.Equal(0, CountWhere("section", $"book_id = {gone.BookId}"));
        Assert.Null(_db.Jobs.Get(gone.BookId));
        Assert.Equal(keptChunks, _db.Chunks.CountChunks());
        Assert.Equal(keptChunks, _db.Chunks.CountVectors());
        Assert.Equal(keptChunks, _db.Chunks.CountFts());
    }

    // ---- end to end with a fake embedder ------------------------------------------------------------------

    [Fact]
    public async Task Ingest_AliceEpub_EndToEnd()
    {
        var service = Service();
        var added = await service.AddAsync(Fixture("alice.epub"));
        var progress = new List<IngestProgress>();

        var result = await service.IngestAsync(added.BookId, new Progress<IngestProgress>(progress.Add));

        var book = _db.Books.GetById(added.BookId)!;
        Assert.Equal(BookStatus.Ready, result.Status);
        Assert.Equal("Alice's Adventures in Wonderland", book.Title);
        Assert.Equal("Lewis Carroll", book.Author);
        Assert.Equal("en", book.Language);
        Assert.Equal("fake-embedder", book.EmbeddingModelId);
        Assert.InRange(_db.Chunks.CountChunks(), 50, 120);               // ~27k words at ~500 words per chunk
        Assert.True(_db.Sections.ListByBook(added.BookId).Count >= 12);
        Assert.All(ChunkSectionIds(added.BookId), id => Assert.NotNull(id));
        Assert.Equal(1.0, _db.Jobs.Get(added.BookId)!.Progress);
        Assert.True(result.Timings.Extract > TimeSpan.Zero);
        Assert.Equal(_db.Chunks.CountChunks(), result.Timings.Chunks);
        // Progress rises to the end, one report per batch after the initial extract report.
        Assert.Equal(progress[^1].ChunksTotal, progress[^1].ChunksDone);
        Assert.Equal(progress.Select(p => p.ChunksDone).Order(), progress.Select(p => p.ChunksDone));
    }

    private List<long?> ChunkSectionIds(long bookId)
    {
        using var cmd = new SqliteCommand($"SELECT section_id FROM chunk WHERE book_id = {bookId}", _db.Conn);
        using var r = cmd.ExecuteReader();
        var list = new List<long?>();
        while (r.Read()) list.Add(r.IsDBNull(0) ? null : r.GetInt64(0));
        return list;
    }

    [Fact]
    public async Task Ingest_Pdf_StoresPageRanges()
    {
        var service = Service();
        var added = await service.AddAsync(Fixture("origin.pdf"));

        await service.IngestAsync(added.BookId);

        using var cmd = new SqliteCommand(
            $"SELECT MIN(page_start), MAX(page_end) FROM chunk WHERE book_id = {added.BookId}", _db.Conn);
        using var r = cmd.ExecuteReader();
        r.Read();
        Assert.Equal((1L, 8L), (r.GetInt64(0), r.GetInt64(1)));
    }

    [Fact]
    public async Task Ingest_AlreadyReadyBook_IsLeftAlone()
    {
        var service = Service();
        var added = await service.AddAsync(Fixture("notes.md"));
        await service.IngestAsync(added.BookId);
        var calls = _embedder.Calls;

        var again = await service.IngestAsync(added.BookId);

        Assert.Equal(BookStatus.Ready, again.Status);
        Assert.Equal(calls, _embedder.Calls);
    }
}
