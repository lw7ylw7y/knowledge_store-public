using System.Globalization;
using BookSearch.Core.Search;
using Microsoft.Data.Sqlite;

namespace BookSearch.Core.Data;

internal static class Sql
{
    public static SqliteCommand Cmd(SqliteConnection conn, SqliteTransaction? tx, string sql,
        params (string name, object? value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static string Stamp(DateTime t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public static DateTime Parse(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

public sealed class BookRepository(SqliteConnection conn)
{
    private const string Columns =
        "id, file_path, file_hash, title, author, format, language, status, status_reason, embedding_model_id, added_at";

    public long Insert(Book book, SqliteTransaction? tx = null)
    {
        using var cmd = Sql.Cmd(conn, tx, """
            INSERT INTO book(file_path, file_hash, title, author, format, language,
                             status, status_reason, embedding_model_id, added_at)
            VALUES($path, $hash, $title, $author, $format, $lang, $status, $reason, $model, $added);
            SELECT last_insert_rowid();
            """,
            ("$path", book.FilePath), ("$hash", book.FileHash), ("$title", book.Title), ("$author", book.Author),
            ("$format", book.Format), ("$lang", book.Language),
            ("$status", book.Status), ("$reason", book.StatusReason), ("$model", book.EmbeddingModelId),
            ("$added", Sql.Stamp(book.AddedAt)));
        return (long)cmd.ExecuteScalar()!;
    }

    public Book? GetById(long id) => Single($"SELECT {Columns} FROM book WHERE id = $id", ("$id", id));

    public Book? GetByHash(string hash) => Single($"SELECT {Columns} FROM book WHERE file_hash = $h", ("$h", hash));

    public List<Book> List()
    {
        using var cmd = Sql.Cmd(conn, null, $"SELECT {Columns} FROM book ORDER BY id");
        using var r = cmd.ExecuteReader();
        var list = new List<Book>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public void SetStatus(long id, string status, string? reason = null, SqliteTransaction? tx = null)
    {
        using var cmd = Sql.Cmd(conn, tx, "UPDATE book SET status = $s, status_reason = $r WHERE id = $id",
            ("$s", status), ("$r", reason), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Records what extraction found, and which embedding model the vectors will come from.</summary>
    public void SetExtracted(long id, string? title, string? author, string? language, string embeddingModelId,
        SqliteTransaction? tx = null)
    {
        using var cmd = Sql.Cmd(conn, tx, """
            UPDATE book SET title = $t, author = $a, language = $l, embedding_model_id = $m
            WHERE id = $id
            """, ("$t", title), ("$a", author), ("$l", language), ("$m", embeddingModelId), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes the book and everything derived from it. Virtual tables don't cascade, so FTS and vector
    /// rows are deleted explicitly before the book row; foreign keys clean up sections, chunks and the job.
    /// </summary>
    public void Delete(long id)
    {
        Database.InTransaction(conn, tx =>
        {
            // External-content FTS5 needs the original text to remove its index entries.
            Sql.Cmd(conn, tx, """
                INSERT INTO chunk_fts(chunk_fts, rowid, text)
                SELECT 'delete', id, text FROM chunk WHERE book_id = $id
                """, ("$id", id)).ExecuteNonQuery();
            Sql.Cmd(conn, tx, "DELETE FROM chunk_fts_cjk WHERE rowid IN (SELECT id FROM chunk WHERE book_id = $id)",
                ("$id", id)).ExecuteNonQuery();
            Sql.Cmd(conn, tx, "DELETE FROM chunk_vec WHERE chunk_id IN (SELECT id FROM chunk WHERE book_id = $id)",
                ("$id", id)).ExecuteNonQuery();
            Sql.Cmd(conn, tx, "DELETE FROM book WHERE id = $id", ("$id", id)).ExecuteNonQuery();
        });
    }

    private Book? Single(string sql, params (string, object?)[] args)
    {
        using var cmd = Sql.Cmd(conn, null, sql, args);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    private static Book Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        FilePath = r.GetString(1),
        FileHash = r.GetString(2),
        Title = Sql.Str(r, 3),
        Author = Sql.Str(r, 4),
        Format = r.GetString(5),
        Language = Sql.Str(r, 6),
        Status = r.GetString(7),
        StatusReason = Sql.Str(r, 8),
        EmbeddingModelId = Sql.Str(r, 9),
        AddedAt = Sql.Parse(r.GetString(10)),
    };
}

public sealed class SectionRepository(SqliteConnection conn)
{
    public long Insert(Section s, SqliteTransaction? tx = null)
    {
        using var cmd = Sql.Cmd(conn, tx, """
            INSERT INTO section(book_id, parent_id, title, ord) VALUES($b, $p, $t, $o);
            SELECT last_insert_rowid();
            """, ("$b", s.BookId), ("$p", s.ParentId), ("$t", s.Title), ("$o", s.Order));
        return (long)cmd.ExecuteScalar()!;
    }

    public List<Section> ListByBook(long bookId)
    {
        using var cmd = Sql.Cmd(conn, null,
            "SELECT id, book_id, parent_id, title, ord FROM section WHERE book_id = $b ORDER BY ord", ("$b", bookId));
        using var r = cmd.ExecuteReader();
        var list = new List<Section>();
        while (r.Read())
            list.Add(new Section
            {
                Id = r.GetInt64(0), BookId = r.GetInt64(1), ParentId = Sql.Long(r, 2),
                Title = Sql.Str(r, 3), Order = r.GetInt32(4),
            });
        return list;
    }
}

public sealed class ChunkRepository(SqliteConnection conn)
{
    /// <summary>
    /// Writes a batch of chunks (row, keyword entries, vector) in one transaction. Callers that also
    /// advance the ingest job should use <see cref="IngestJobRepository.WriteBatch"/>.
    /// </summary>
    public void WriteBatch(IReadOnlyList<ChunkWrite> batch, SqliteTransaction tx)
    {
        foreach (var w in batch)
        {
            var c = w.Chunk;
            using var insert = Sql.Cmd(conn, tx, """
                INSERT INTO chunk(book_id, section_id, chunk_index, text, token_count, page_start, page_end, location)
                VALUES($b, $s, $i, $t, $n, $ps, $pe, $l);
                SELECT last_insert_rowid();
                """,
                ("$b", c.BookId), ("$s", c.SectionId), ("$i", c.ChunkIndex), ("$t", c.Text), ("$n", c.TokenCount),
                ("$ps", c.PageStart), ("$pe", c.PageEnd), ("$l", c.Location));
            var id = (long)insert.ExecuteScalar()!;

            Sql.Cmd(conn, tx, "INSERT INTO chunk_fts(rowid, text) VALUES($id, $t)", ("$id", id), ("$t", c.Text))
                .ExecuteNonQuery();
            if (w.Cjk)
                Sql.Cmd(conn, tx, "INSERT INTO chunk_fts_cjk(rowid, text) VALUES($id, $t)", ("$id", id), ("$t", c.Text))
                    .ExecuteNonQuery();
            Sql.Cmd(conn, tx, "INSERT INTO chunk_vec(chunk_id, book_id, embedding) VALUES($id, $b, $v)",
                ("$id", id), ("$b", c.BookId), ("$v", Database.ToBlob(w.Embedding))).ExecuteNonQuery();
        }
    }

    /// <summary>Nearest chunks by cosine distance (smaller is closer), optionally limited to one book.</summary>
    public List<VectorHit> Knn(float[] query, int k, long? bookId = null)
    {
        var sql = "SELECT chunk_id, distance FROM chunk_vec WHERE embedding MATCH $q AND k = $k" +
                  (bookId is null ? "" : " AND book_id = $b") + " ORDER BY distance";
        using var cmd = Sql.Cmd(conn, null, sql, ("$q", Database.ToBlob(query)), ("$k", k));
        if (bookId is not null) cmd.Parameters.AddWithValue("$b", bookId.Value);
        using var r = cmd.ExecuteReader();
        var hits = new List<VectorHit>();
        while (r.Read()) hits.Add(new VectorHit(r.GetInt64(0), r.GetDouble(1)));
        return hits;
    }

    /// <summary>The nearest chunks within a few books (one query per book, merged), best first.</summary>
    public List<VectorHit> Knn(float[] query, int k, IReadOnlyCollection<long> bookIds) =>
        bookIds.SelectMany(id => Knn(query, k, id)).OrderBy(h => h.Distance).Take(k).ToList();

    /// <summary>Ranked chunk ids for an FTS5 MATCH expression on the word or trigram index, optionally within some books only.</summary>
    public List<long> FtsSearch(string matchExpression, int limit, bool cjk = false, IReadOnlyCollection<long>? bookIds = null)
    {
        var table = cjk ? "chunk_fts_cjk" : "chunk_fts";
        // The ids are our own numbers, so they can go straight into the statement.
        var inBooks = bookIds is null ? "" : $" AND rowid IN (SELECT id FROM chunk WHERE book_id IN ({string.Join(",", bookIds)}))";
        using var cmd = Sql.Cmd(conn, null,
            $"SELECT rowid FROM {table} WHERE {table} MATCH $q{inBooks} ORDER BY rank LIMIT $n",
            ("$q", matchExpression), ("$n", limit));
        using var r = cmd.ExecuteReader();
        var ids = new List<long>();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    /// <summary>
    /// How many chunks an FTS5 term is in, counting no further than <paramref name="cap"/> (so a common word costs
    /// only as much as the cap).
    /// </summary>
    public int CountFtsMatches(string term, bool cjk, int cap)
    {
        var table = cjk ? "chunk_fts_cjk" : "chunk_fts";
        using var cmd = Sql.Cmd(conn, null,
            $"SELECT COUNT(*) FROM (SELECT rowid FROM {table} WHERE {table} MATCH $q LIMIT $n)",
            ("$q", "\"" + term.Replace("\"", "\"\"") + "\""), ("$n", cap));
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>Chunk text with its book, author and section title, for rendering search results.</summary>
    public Dictionary<long, Passage> GetPassages(IReadOnlyCollection<long> chunkIds)
    {
        var result = new Dictionary<long, Passage>();
        if (chunkIds.Count == 0) return result;
        using var cmd = conn.CreateCommand();
        var names = chunkIds.Select((_, i) => $"$p{i}").ToList();
        cmd.CommandText = $"""
            SELECT c.id, c.book_id, b.title, b.author, s.title, c.text, c.page_start, c.page_end, c.location, b.language,
                   c.chunk_index, c.section_id
            FROM chunk c
            JOIN book b ON b.id = c.book_id
            LEFT JOIN section s ON s.id = c.section_id
            WHERE c.id IN ({string.Join(",", names)})
            """;
        foreach (var (id, name) in chunkIds.Zip(names)) cmd.Parameters.AddWithValue(name, id);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetInt64(0)] = new Passage(r.GetInt64(0), r.GetInt64(1), Sql.Str(r, 2), Sql.Str(r, 3), Sql.Str(r, 4),
                r.GetString(5), Sql.Int(r, 6), Sql.Int(r, 7), Sql.Str(r, 8), Sql.Str(r, 9), r.GetInt32(10), Sql.Long(r, 11));
        return result;
    }

    public Chunk? GetById(long id)
    {
        using var cmd = Sql.Cmd(conn, null, """
            SELECT id, book_id, section_id, chunk_index, text, token_count, page_start, page_end, location
            FROM chunk WHERE id = $id
            """, ("$id", id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Chunk
        {
            Id = r.GetInt64(0), BookId = r.GetInt64(1), SectionId = Sql.Long(r, 2), ChunkIndex = r.GetInt32(3),
            Text = r.GetString(4), TokenCount = r.GetInt32(5), PageStart = Sql.Int(r, 6), PageEnd = Sql.Int(r, 7),
            Location = Sql.Str(r, 8),
        };
    }

    /// <summary>
    /// The chunks of one book with indexes from <paramref name="fromIndex"/> to <paramref name="toIndex"/> that belong to the same
    /// section, in reading order (<paramref name="sectionId"/> null means chunks that belong to no section).
    /// </summary>
    public List<Chunk> GetRun(long bookId, long? sectionId, int fromIndex, int toIndex)
    {
        var sectionFilter = sectionId is null ? "section_id IS NULL" : "section_id = $s";
        using var cmd = Sql.Cmd(conn, null,
            "SELECT id, book_id, section_id, chunk_index, text, token_count, page_start, page_end, location " +
            $"FROM chunk WHERE book_id = $b AND {sectionFilter} AND chunk_index BETWEEN $lo AND $hi ORDER BY chunk_index",
            ("$b", bookId), ("$lo", fromIndex), ("$hi", toIndex));
        if (sectionId is not null) cmd.Parameters.AddWithValue("$s", sectionId.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<Chunk>();
        while (r.Read())
            list.Add(new Chunk
            {
                Id = r.GetInt64(0), BookId = r.GetInt64(1), SectionId = Sql.Long(r, 2), ChunkIndex = r.GetInt32(3),
                Text = r.GetString(4), TokenCount = r.GetInt32(5), PageStart = Sql.Int(r, 6), PageEnd = Sql.Int(r, 7),
                Location = Sql.Str(r, 8),
            });
        return list;
    }

    /// <summary>A section's chunks in reading order; <paramref name="sectionId"/> null means the chunks that belong to no section.</summary>
    public List<Chunk> ListBySection(long bookId, long? sectionId)
    {
        var sectionFilter = sectionId is null ? "section_id IS NULL" : "section_id = $s";
        using var cmd = Sql.Cmd(conn, null,
            "SELECT id, book_id, section_id, chunk_index, text, token_count, page_start, page_end, location " +
            $"FROM chunk WHERE book_id = $b AND {sectionFilter} ORDER BY chunk_index", ("$b", bookId));
        if (sectionId is not null) cmd.Parameters.AddWithValue("$s", sectionId.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<Chunk>();
        while (r.Read())
            list.Add(new Chunk
            {
                Id = r.GetInt64(0), BookId = r.GetInt64(1), SectionId = Sql.Long(r, 2), ChunkIndex = r.GetInt32(3),
                Text = r.GetString(4), TokenCount = r.GetInt32(5), PageStart = Sql.Int(r, 6), PageEnd = Sql.Int(r, 7),
                Location = Sql.Str(r, 8),
            });
        return list;
    }

    /// <summary>The sections that have chunks, in the order they are read (null stands for chunks without a section).</summary>
    public List<long?> SectionsInReadingOrder(long bookId)
    {
        using var cmd = Sql.Cmd(conn, null,
            "SELECT section_id, MIN(chunk_index) AS first FROM chunk WHERE book_id = $b GROUP BY section_id ORDER BY first",
            ("$b", bookId));
        using var r = cmd.ExecuteReader();
        var list = new List<long?>();
        while (r.Read()) list.Add(Sql.Long(r, 0));
        return list;
    }

    public long CountChunks(long? bookId = null) => Count("chunk", bookId);
    public long CountFts(long? bookId = null) => CountByChunk("chunk_fts", bookId);
    public long CountFtsCjk(long? bookId = null) => CountByChunk("chunk_fts_cjk", bookId);
    public long CountVectors(long? bookId = null) => Count("chunk_vec", bookId);

    private long Count(string table, long? bookId)
    {
        using var cmd = Sql.Cmd(conn, null,
            $"SELECT COUNT(*) FROM {table}" + (bookId is null ? "" : " WHERE book_id = $b"));
        if (bookId is not null) cmd.Parameters.AddWithValue("$b", bookId.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    // FTS tables have no book_id column; count the rows whose rowid still belongs to a chunk of the book,
    // or (for orphan detection) all rows when no book is given.
    private long CountByChunk(string table, long? bookId)
    {
        // External-content tables read through to `chunk`, so count via the index's docsize shadow table,
        // which only has rows for entries that were actually indexed.
        using var cmd = Sql.Cmd(conn, null, bookId is null
            ? $"SELECT COUNT(*) FROM {table}_docsize"
            : $"SELECT COUNT(*) FROM {table}_docsize WHERE id IN (SELECT id FROM chunk WHERE book_id = $b)");
        if (bookId is not null) cmd.Parameters.AddWithValue("$b", bookId.Value);
        return (long)cmd.ExecuteScalar()!;
    }
}

public sealed class IngestJobRepository(SqliteConnection conn)
{
    public void Insert(long bookId, string stage = IngestStage.Extract, SqliteTransaction? tx = null)
    {
        Sql.Cmd(conn, tx, "INSERT INTO ingest_job(book_id, stage, updated_at) VALUES($b, $s, $u)",
            ("$b", bookId), ("$s", stage), ("$u", Sql.Stamp(DateTime.UtcNow))).ExecuteNonQuery();
    }

    public IngestJob? Get(long bookId)
    {
        using var cmd = Sql.Cmd(conn, null,
            "SELECT book_id, stage, next_chunk_index, progress, error, updated_at, attempts FROM ingest_job WHERE book_id = $b",
            ("$b", bookId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>Jobs the worker should (re)start on launch: everything not <c>done</c>.</summary>
    public List<IngestJob> ListUnfinished()
    {
        using var cmd = Sql.Cmd(conn, null,
            "SELECT book_id, stage, next_chunk_index, progress, error, updated_at, attempts FROM ingest_job WHERE stage <> 'done' ORDER BY book_id");
        using var r = cmd.ExecuteReader();
        var list = new List<IngestJob>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>Books the worker should index: queued or interrupted ones (not failed, rejected or ready), oldest first.</summary>
    public List<IngestJob> ListRunnable()
    {
        using var cmd = Sql.Cmd(conn, null, """
            SELECT j.book_id, j.stage, j.next_chunk_index, j.progress, j.error, j.updated_at, j.attempts
            FROM ingest_job j JOIN book b ON b.id = j.book_id
            WHERE j.stage <> 'done' AND b.status IN ('queued', 'indexing')
            ORDER BY j.book_id
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<IngestJob>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>Counts a start of this book. Cleared by the next written batch, so only starts that got nowhere add up.</summary>
    public int BeginAttempt(long bookId)
    {
        Sql.Cmd(conn, null, "UPDATE ingest_job SET attempts = attempts + 1, updated_at = $u WHERE book_id = $b",
            ("$u", Sql.Stamp(DateTime.UtcNow)), ("$b", bookId)).ExecuteNonQuery();
        return Get(bookId)?.Attempts ?? 0;
    }

    public void ResetAttempts(long bookId) =>
        Sql.Cmd(conn, null, "UPDATE ingest_job SET attempts = 0 WHERE book_id = $b", ("$b", bookId)).ExecuteNonQuery();

    public void SetStage(long bookId, string stage, double? progress = null, string? error = null,
        SqliteTransaction? tx = null)
    {
        Sql.Cmd(conn, tx, """
            UPDATE ingest_job SET stage = $s, progress = COALESCE($p, progress), error = $e, updated_at = $u
            WHERE book_id = $b
            """,
            ("$s", stage), ("$p", progress), ("$e", error), ("$u", Sql.Stamp(DateTime.UtcNow)), ("$b", bookId))
            .ExecuteNonQuery();
    }

    /// <summary>
    /// Writes a batch of chunks and advances <c>next_chunk_index</c> in one transaction, so a crash
    /// can never leave a partially written batch or a stale cursor.
    /// </summary>
    public void WriteBatch(long bookId, IReadOnlyList<ChunkWrite> batch, int nextChunkIndex, double progress)
    {
        Database.InTransaction(conn, tx =>
        {
            new ChunkRepository(conn).WriteBatch(batch, tx);
            Sql.Cmd(conn, tx, """
                UPDATE ingest_job SET next_chunk_index = $n, progress = $p, attempts = 0, updated_at = $u WHERE book_id = $b
                """,
                ("$n", nextChunkIndex), ("$p", progress), ("$u", Sql.Stamp(DateTime.UtcNow)), ("$b", bookId))
                .ExecuteNonQuery();
        });
    }

    private static IngestJob Read(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetDouble(3), Sql.Str(r, 4), Sql.Parse(r.GetString(5)),
        r.GetInt32(6));
}

public sealed class WorkerControlRepository(SqliteConnection conn)
{
    public WorkerControl Get()
    {
        using var cmd = Sql.Cmd(conn, null, "SELECT paused, cpu_cap_percent FROM worker_control WHERE id = 1");
        using var r = cmd.ExecuteReader();
        r.Read();
        return new WorkerControl(r.GetInt32(0) != 0, r.GetInt32(1));
    }

    public void SetPaused(bool paused) =>
        Sql.Cmd(conn, null, "UPDATE worker_control SET paused = $p WHERE id = 1", ("$p", paused ? 1 : 0))
            .ExecuteNonQuery();

    public void SetCpuCap(int percent)
    {
        if (percent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        Sql.Cmd(conn, null, "UPDATE worker_control SET cpu_cap_percent = $c WHERE id = 1", ("$c", percent))
            .ExecuteNonQuery();
    }
}

/// <summary>The search history (ST-23, ST-24): threads of questions, each with the date and time and the summary text. Independent of the library tables.</summary>
public sealed class SearchHistoryRepository(SqliteConnection conn)
{
    private const string Columns = "id, question, created_at, summary_status, summary_text, summary_model, summary_ms, thread_id, filter, searched_as, also_searched";

    public long AddThread(DateTime? at = null)
    {
        var stamp = Sql.Stamp(at ?? DateTime.UtcNow);
        using var cmd = Sql.Cmd(conn, null, """
            INSERT INTO search_thread(created_at, updated_at) VALUES($t, $t);
            SELECT last_insert_rowid();
            """, ("$t", stamp));
        return (long)cmd.ExecuteScalar()!;
    }

    /// <param name="searchedAs">The follow-up rewritten to stand on its own, when it was; what was searched and summarized instead of <paramref name="question"/>.</param>
    /// <param name="alsoSearched">The question in the other languages it was also searched in (DCR-11); kept so a restart searches the same.</param>
    public long Add(string question, string status, DateTime? createdAt = null, long? threadId = null, string? filter = null,
        string? searchedAs = null, IReadOnlyList<string>? alsoSearched = null)
    {
        var at = createdAt ?? DateTime.UtcNow;
        using var cmd = Sql.Cmd(conn, null, """
            INSERT INTO search_history(question, created_at, summary_status, thread_id, filter, searched_as, also_searched)
            VALUES($q, $t, $s, $th, $f, $sa, $as);
            SELECT last_insert_rowid();
            """, ("$q", question), ("$t", Sql.Stamp(at)), ("$s", status), ("$th", threadId), ("$f", filter), ("$sa", searchedAs),
            ("$as", alsoSearched is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(alsoSearched) : null));
        var id = (long)cmd.ExecuteScalar()!;
        if (threadId is { } thread)
            Sql.Cmd(conn, null, "UPDATE search_thread SET updated_at = $t WHERE id = $id", ("$t", Sql.Stamp(at)), ("$id", thread)).ExecuteNonQuery();
        return id;
    }

    /// <summary>Sets the status and, when given, the summary text. Does nothing if the entry has been closed.</summary>
    public void Update(long id, string status, string? summaryText = null, string? model = null, int? ms = null)
    {
        Sql.Cmd(conn, null, """
            UPDATE search_history
            SET summary_status = $s, summary_text = COALESCE($t, summary_text), summary_model = COALESCE($m, summary_model),
                summary_ms = COALESCE($ms, summary_ms)
            WHERE id = $id
            """, ("$s", status), ("$t", summaryText), ("$m", model), ("$ms", ms), ("$id", id)).ExecuteNonQuery();
    }

    public SearchHistoryEntry? Get(long id)
    {
        using var cmd = Sql.Cmd(conn, null, $"SELECT {Columns} FROM search_history WHERE id = $id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>Every question, newest first.</summary>
    public List<SearchHistoryEntry> List()
    {
        using var cmd = Sql.Cmd(conn, null, $"SELECT {Columns} FROM search_history ORDER BY id DESC");
        using var r = cmd.ExecuteReader();
        var list = new List<SearchHistoryEntry>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>The threads, the one with the most recent question first.</summary>
    public List<SearchThread> ListThreads()
    {
        using var cmd = Sql.Cmd(conn, null, """
            SELECT t.id, t.created_at, t.updated_at,
                   (SELECT h.question FROM search_history h WHERE h.thread_id = t.id ORDER BY h.id LIMIT 1),
                   (SELECT COUNT(*) FROM search_history h WHERE h.thread_id = t.id)
            FROM search_thread t ORDER BY t.updated_at DESC, t.id DESC
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<SearchThread>();
        while (r.Read())
            if (r.GetInt32(4) > 0)
                list.Add(new SearchThread(r.GetInt64(0), Sql.Parse(r.GetString(1)), Sql.Parse(r.GetString(2)), r.GetString(3), r.GetInt32(4)));
        return list;
    }

    public bool ThreadExists(long id)
    {
        using var cmd = Sql.Cmd(conn, null, "SELECT COUNT(*) FROM search_thread WHERE id = $id", ("$id", id));
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>One thread's questions, in the order they were asked.</summary>
    public List<SearchHistoryEntry> ListThread(long threadId)
    {
        using var cmd = Sql.Cmd(conn, null, $"SELECT {Columns} FROM search_history WHERE thread_id = $t ORDER BY id", ("$t", threadId));
        using var r = cmd.ExecuteReader();
        var list = new List<SearchHistoryEntry>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>Entries still waiting or being written, oldest first (the order they were searched).</summary>
    public List<SearchHistoryEntry> ListUnfinished()
    {
        using var cmd = Sql.Cmd(conn, null,
            $"SELECT {Columns} FROM search_history WHERE summary_status IN ('waiting', 'writing') ORDER BY id");
        using var r = cmd.ExecuteReader();
        var list = new List<SearchHistoryEntry>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>
    /// Deletes one question. Its thread stays even if it is now empty (a question is replaced inside its thread when the user
    /// removes the context or scope chip); <see cref="ListThreads"/> leaves empty threads out.
    /// </summary>
    public void Delete(long id) => Sql.Cmd(conn, null, "DELETE FROM search_history WHERE id = $id", ("$id", id)).ExecuteNonQuery();

    /// <summary>Deletes a whole thread with all its questions.</summary>
    public void DeleteThread(long threadId)
    {
        Sql.Cmd(conn, null, "DELETE FROM search_history WHERE thread_id = $t", ("$t", threadId)).ExecuteNonQuery();
        Sql.Cmd(conn, null, "DELETE FROM search_thread WHERE id = $t", ("$t", threadId)).ExecuteNonQuery();
    }

    public void Clear()
    {
        Sql.Cmd(conn, null, "DELETE FROM search_history").ExecuteNonQuery();
        Sql.Cmd(conn, null, "DELETE FROM search_thread").ExecuteNonQuery();
    }

    private static SearchHistoryEntry Read(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), Sql.Parse(r.GetString(2)), r.GetString(3), Sql.Str(r, 4), Sql.Str(r, 5), Sql.Int(r, 6),
        Sql.Long(r, 7), Sql.Str(r, 8), Sql.Str(r, 9), ParseList(Sql.Str(r, 10)));

    private static IReadOnlyList<string>? ParseList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

/// <summary>Where the reader stopped in a book: the chapter's number (from 0) and the first visible paragraph in it (from 0).</summary>
public sealed record ReadingPosition(long BookId, int SectionPos, int Paragraph);

/// <summary>One row per book, written with a single upsert, so saving a place never touches another book's (DCR-10, ST-35).</summary>
public sealed class ReadingPositionRepository(SqliteConnection conn)
{
    public ReadingPosition? Get(long bookId)
    {
        using var cmd = Sql.Cmd(conn, null, "SELECT section_pos, paragraph FROM reading_position WHERE book_id = $b", ("$b", bookId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new ReadingPosition(bookId, r.GetInt32(0), r.GetInt32(1)) : null;
    }

    /// <summary>The saved place of every book that has one.</summary>
    public Dictionary<long, ReadingPosition> All()
    {
        using var cmd = Sql.Cmd(conn, null, "SELECT book_id, section_pos, paragraph FROM reading_position");
        using var r = cmd.ExecuteReader();
        var all = new Dictionary<long, ReadingPosition>();
        while (r.Read()) all[r.GetInt64(0)] = new ReadingPosition(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2));
        return all;
    }

    /// <summary>How many chapters one book has (a lookup by the book's index, not a scan of the chunk table).</summary>
    public int SectionCount(long bookId)
    {
        using var cmd = Sql.Cmd(conn, null,
            "SELECT COUNT(*) FROM (SELECT DISTINCT section_id FROM chunk WHERE book_id = $b)", ("$b", bookId));
        return (int)(long)cmd.ExecuteScalar()!;
    }

    public void Save(long bookId, int sectionPos, int paragraph)
    {
        using var cmd = Sql.Cmd(conn, null, """
            INSERT INTO reading_position(book_id, section_pos, paragraph, updated_at)
            SELECT $b, $s, $p, $t WHERE EXISTS (SELECT 1 FROM book WHERE id = $b)
            ON CONFLICT(book_id) DO UPDATE SET section_pos = $s, paragraph = $p, updated_at = $t
            """, ("$b", bookId), ("$s", Math.Max(0, sectionPos)), ("$p", Math.Max(0, paragraph)),
            ("$t", DateTime.UtcNow.ToString("O")));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// How many chapters (sections that have text) each book that has a saved place has, for the "Section n of m" line. Only
    /// those books are counted: the chunk table is the largest one, and most books are never opened.
    /// </summary>
    public Dictionary<long, int> SectionCounts()
    {
        using var cmd = Sql.Cmd(conn, null,
            "SELECT book_id, COUNT(*) FROM (SELECT DISTINCT book_id, section_id FROM chunk " +
            "WHERE book_id IN (SELECT book_id FROM reading_position)) GROUP BY book_id");
        using var r = cmd.ExecuteReader();
        var counts = new Dictionary<long, int>();
        while (r.Read()) counts[r.GetInt64(0)] = r.GetInt32(1);
        return counts;
    }
}
