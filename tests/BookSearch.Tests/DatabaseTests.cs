using BookSearch.Core.Data;
using Microsoft.Data.Sqlite;

namespace BookSearch.Tests;

public class DatabaseTests
{
    [Fact]
    public void Db_Open_LoadsVecExtension()
    {
        using var db = new TestDb();
        Assert.Equal(Database.PinnedVecVersion, Database.VecVersion(db.Conn));
    }

    [Fact]
    public void Db_Migrate_CreatesAllTables()
    {
        using var db = new TestDb();
        string[] expected =
        [
            "book", "section", "chunk", "chunk_fts", "chunk_fts_cjk", "chunk_vec",
            "ingest_job", "worker_control", "app_meta",
        ];
        using var cmd = new SqliteCommand("SELECT name FROM sqlite_master WHERE type='table'", db.Conn);
        using var r = cmd.ExecuteReader();
        var names = new HashSet<string>();
        while (r.Read()) names.Add(r.GetString(0));
        foreach (var table in expected) Assert.Contains(table, names);

        Assert.Equal(Migrator.LatestVersion, Migrator.CurrentVersion(db.Conn));
        var wal = new SqliteCommand("PRAGMA journal_mode", db.Conn).ExecuteScalar();
        Assert.Equal("wal", wal);
        Assert.Equal(1L, new SqliteCommand("PRAGMA foreign_keys", db.Conn).ExecuteScalar());

        var again = Migrator.Migrate(db.Conn);
        Assert.False(again.Changed);
        Assert.Equal(1L, db.Count("worker_control"));
    }

    [Fact]
    public void Vec_KnnQuery_ReturnsNearestFirst()
    {
        using var db = new TestDb();
        var book = db.AddBook();
        db.Add(
            db.Write(book, 0, "a", TestDb.Vec(1, 0)),
            db.Write(book, 1, "b", TestDb.Vec(1, 1)),
            db.Write(book, 2, "c", TestDb.Vec(0, 1)));

        var hits = db.Chunks.Knn(TestDb.Vec(1, 0), 3);

        Assert.Equal(3, hits.Count);
        Assert.Equal("a", db.Chunks.GetById(hits[0].ChunkId)!.Text);
        Assert.Equal("b", db.Chunks.GetById(hits[1].ChunkId)!.Text);
        Assert.Equal("c", db.Chunks.GetById(hits[2].ChunkId)!.Text);
        Assert.Equal(0.0, hits[0].Distance, 5);
        Assert.Equal(1 - Math.Cos(Math.PI / 4), hits[1].Distance, 5);
        Assert.Equal(1.0, hits[2].Distance, 5);
    }

    [Fact]
    public void Vec_PartitionFilter_LimitsToBook()
    {
        using var db = new TestDb();
        var one = db.AddBook("one");
        var two = db.AddBook("two");
        db.Add(
            db.Write(one, 0, "one-a", TestDb.Vec(1, 0)),
            db.Write(one, 1, "one-b", TestDb.Vec(0, 1)),
            db.Write(two, 0, "two-a", TestDb.Vec(1, 0)));

        var hits = db.Chunks.Knn(TestDb.Vec(1, 0), 10, two);

        var hit = Assert.Single(hits);
        Assert.Equal(two, db.Chunks.GetById(hit.ChunkId)!.BookId);
    }

    [Fact]
    public void Fts_Match_FindsDiacriticFolded()
    {
        using var db = new TestDb();
        var book = db.AddBook();
        db.Add(db.Write(book, 0, "We met at the café downtown.", TestDb.Vec(1)));

        Assert.Single(db.Chunks.FtsSearch("cafe", 10));
        Assert.Single(db.Chunks.FtsSearch("café", 10));
    }

    [Fact]
    public void FtsCjk_Trigram_FindsJapaneseSubstring()
    {
        using var db = new TestDb();
        var book = db.AddBook();
        db.Add(db.Write(book, 0, "私はその人を常に先生と呼んでいた。", TestDb.Vec(1), cjk: true));

        Assert.Single(db.Chunks.FtsSearch("先生と", 10, cjk: true));
    }

    [Fact]
    public void Book_Delete_RemovesChunksFtsAndVectors()
    {
        using var db = new TestDb();
        var keep = db.AddBook("keep");
        var gone = db.AddBook("gone");
        db.Sections.Insert(new Section { BookId = gone, Title = "Ch 1", Order = 0 });
        db.Jobs.Insert(gone);
        db.Add(
            db.Write(keep, 0, "kept text", TestDb.Vec(1)),
            db.Write(gone, 0, "doomed alpha", TestDb.Vec(0, 1)),
            db.Write(gone, 1, "doomed 日本語の本文", TestDb.Vec(0, 0, 1), cjk: true));

        db.Books.Delete(gone);

        Assert.Equal(0, db.Chunks.CountChunks(gone));
        Assert.Equal(0, db.Chunks.CountVectors(gone));
        Assert.Empty(db.Sections.ListByBook(gone));
        Assert.Null(db.Jobs.Get(gone));
        Assert.Null(db.Books.GetById(gone));
        // Table-wide: only the kept book's rows remain, in every index.
        Assert.Equal(1, db.Count("chunk"));
        Assert.Equal(1, db.Count("chunk_vec"));
        Assert.Equal(1, db.Chunks.CountFts());
        Assert.Equal(0, db.Chunks.CountFtsCjk());
        Assert.Empty(db.Chunks.FtsSearch("doomed", 10));
        Assert.Single(db.Chunks.FtsSearch("kept", 10));
    }

    [Fact]
    public void Db_CrashMidTransaction_LeavesNoPartialRows()
    {
        using var db = new TestDb();
        var book = db.AddBook();
        db.Jobs.Insert(book);

        Assert.Throws<InvalidOperationException>(() => Database.InTransaction(db.Conn, tx =>
        {
            db.Chunks.WriteBatch([db.Write(book, 0, "first", TestDb.Vec(1)), db.Write(book, 1, "second", TestDb.Vec(0, 1))], tx);
            throw new InvalidOperationException("simulated crash");
        }));

        Assert.Equal(0, db.Count("chunk"));
        Assert.Equal(0, db.Count("chunk_vec"));
        Assert.Equal(0, db.Chunks.CountFts());

        // A bad vector halfway through a job batch also rolls the cursor back with it.
        var bad = new ChunkWrite(
            new Chunk { BookId = book, ChunkIndex = 1, Text = "bad", TokenCount = 1 }, new float[3]);
        Assert.Throws<ArgumentException>(() =>
            db.Jobs.WriteBatch(book, [db.Write(book, 0, "ok", TestDb.Vec(1)), bad], nextChunkIndex: 2, progress: 1));
        Assert.Equal(0, db.Count("chunk"));
        Assert.Equal(0, db.Jobs.Get(book)!.NextChunkIndex);
    }

    [Fact]
    public void Ingest_Job_BatchAdvancesCursorAndRejectsDuplicateIndexes()
    {
        using var db = new TestDb();
        var book = db.AddBook();
        db.Jobs.Insert(book);

        db.Jobs.WriteBatch(book, [db.Write(book, 0, "a", TestDb.Vec(1))], nextChunkIndex: 1, progress: 0.5);

        Assert.Equal(1, db.Jobs.Get(book)!.NextChunkIndex);
        Assert.Single(db.Jobs.ListUnfinished());
        Assert.ThrowsAny<SqliteException>(() =>
            db.Jobs.WriteBatch(book, [db.Write(book, 0, "dup", TestDb.Vec(1))], 1, 0.5));
        Assert.Equal(1, db.Count("chunk"));
    }

    [Fact]
    public void WorkerControl_DefaultsAndUpdates()
    {
        using var db = new TestDb();
        var control = new WorkerControlRepository(db.Conn);
        Assert.Equal(new WorkerControl(false, 100), control.Get());
        control.SetPaused(true);
        control.SetCpuCap(25);
        Assert.Equal(new WorkerControl(true, 25), control.Get());
    }
}
