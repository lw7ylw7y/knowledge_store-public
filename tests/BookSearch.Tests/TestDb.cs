using BookSearch.Core.Data;
using Microsoft.Data.Sqlite;

namespace BookSearch.Tests;

/// <summary>A migrated library.db in a temp folder, deleted on dispose.</summary>
public sealed class TestDb : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "booksearch-tests-" + Guid.NewGuid().ToString("N"));
    private int _hash;

    public SqliteConnection Conn { get; }
    public BookRepository Books { get; }
    public SectionRepository Sections { get; }
    public ChunkRepository Chunks { get; }
    public IngestJobRepository Jobs { get; }

    public TestDb()
    {
        Conn = Database.Open(Path.Combine(_dir, "library.db"));
        Migrator.Migrate(Conn);
        Books = new BookRepository(Conn);
        Sections = new SectionRepository(Conn);
        Chunks = new ChunkRepository(Conn);
        Jobs = new IngestJobRepository(Conn);
    }

    public long AddBook(string title = "Book", string status = BookStatus.Ready) =>
        Books.Insert(new Book
        {
            FilePath = $"/books/{title}.epub", FileHash = "hash" + _hash++, Title = title, Format = "epub", Status = status,
        });

    public static float[] Vec(params float[] head)
    {
        var v = new float[Database.EmbeddingDimension];
        head.CopyTo(v, 0);
        return v;
    }

    public ChunkWrite Write(long bookId, int index, string text, float[] vec, bool cjk = false) =>
        new(new Chunk { BookId = bookId, ChunkIndex = index, Text = text, TokenCount = 10 }, vec, cjk);

    public void Add(params ChunkWrite[] batch) =>
        Database.InTransaction(Conn, tx => Chunks.WriteBatch(batch, tx));

    public long Count(string table) => (long)new SqliteCommand($"SELECT COUNT(*) FROM {table}", Conn).ExecuteScalar()!;

    public void Dispose()
    {
        Conn.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
