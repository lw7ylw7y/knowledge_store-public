using BookSearch.Core.Data;
using BookSearch.Core.Ingest;
using BookSearch.Core.Worker;
using Microsoft.Data.Sqlite;

namespace BookSearch.App;

/// <summary>A book as the Library page shows it.</summary>
/// <param name="FilePath">The original file, which a book that is not ready opens in its default program (ST-35).</param>
/// <param name="Place">Where the reader stopped and how many chapters the book has; null if it was never opened.</param>
public sealed record BookEntry(long Id, string Title, string? Author, string? Language, string Status, string? Reason,
    string Stage, double Progress, IReadOnlyList<string>? Tags = null, string FilePath = "", ReadingPlace? Place = null);

/// <summary>The saved place of a book, as "Section n of m" shows it.</summary>
public sealed record ReadingPlace(int SectionPos, int SectionCount);

/// <summary>Result of asking for the background worker to run.</summary>
/// <param name="Problem">Why it can't run (for example llama-server is missing); null if it is running or not needed.</param>
public sealed record WorkerStart(string? Problem);

public interface ILibraryService
{
    Task<IReadOnlyList<BookEntry>> ListAsync();
    Task<BookEntry?> GetAsync(long bookId);

    /// <summary>Hashes the file and queues it. Doesn't index: the worker does that.</summary>
    Task<AddResult> AddAsync(string path, CancellationToken ct = default);

    /// <summary>Deletes the book with its chunks, full-text entries and vectors.</summary>
    Task RemoveAsync(long bookId);

    /// <summary>The saved place of every book that has one (for the "Continue" button and "Section n of m"), by book id.</summary>
    Task<IReadOnlyDictionary<long, ReadingPlace>> ListPlacesAsync();

    /// <summary>Books whose status or progress changed since the last call.</summary>
    Task<IReadOnlyList<BookProgress>> PollAsync();

    Task<bool> GetPausedAsync();
    Task SetPausedAsync(bool paused);

    /// <summary>Starts the worker if there is something to index.</summary>
    Task<WorkerStart> EnsureWorkerAsync();

    /// <summary>Every tag in use, by name.</summary>
    Task<IReadOnlyList<string>> ListTagsAsync();

    /// <summary>Replaces the book's tags.</summary>
    Task SetTagsAsync(long bookId, IReadOnlyList<string> tags);

    /// <summary>Adds tags to several books; a book that has one already keeps it.</summary>
    Task AddTagsAsync(IReadOnlyList<long> bookIds, IReadOnlyList<string> tags);

    /// <summary>The worker keeps crashing before it can do anything; the app should tell the user.</summary>
    bool WorkerGaveUp { get; }
}

/// <summary>The indexing speed limit the worker reads before every batch (ST-18).</summary>
public interface IIndexingControl
{
    Task<int> GetCpuCapAsync();
    Task SetCpuCapAsync(int percent);
}

/// <summary>
/// The Library page's door to library.db and the worker. It keeps separate connections for writing, reading and the
/// worker host, because one SQLite connection must not be used from two threads at once.
/// </summary>
public sealed class LibraryService : ILibraryService, IIndexingControl, IDisposable
{
    private readonly SqliteConnection _write, _read, _hostConn;
    private readonly SemaphoreSlim _writeLock = new(1, 1), _readLock = new(1, 1), _hostLock = new(1, 1);
    private readonly IngestService _ingest;
    private readonly BookRepository _books;
    private readonly WorkerControlRepository _control;
    private readonly BookRepository _readBooks;
    private readonly IngestJobRepository _readJobs;
    private readonly IngestProgressPoller _poller;
    private readonly WorkerHost _host;
    private readonly Func<string?> _checkPrerequisites;

    /// <param name="launcher">Starts the worker program.</param>
    /// <param name="checkPrerequisites">Returns why indexing can't run right now (llama-server or a model is missing), or null.</param>
    public LibraryService(AppPaths paths, IWorkerLauncher launcher, Func<string?> checkPrerequisites)
    {
        _write = Open(paths);
        _read = Open(paths);
        _hostConn = Open(paths);
        _checkPrerequisites = checkPrerequisites;

        // The app only queues books, so the embedder and tokenizer are never called here.
        _ingest = new IngestService(_write, QueueOnly.Instance, QueueOnly.Instance);
        _books = new BookRepository(_write);
        _control = new WorkerControlRepository(_write);
        _readBooks = new BookRepository(_read);
        _readJobs = new IngestJobRepository(_read);
        _poller = new IngestProgressPoller(_read);
        _host = new WorkerHost(new IngestJobRepository(_hostConn), launcher);
    }

    private static SqliteConnection Open(AppPaths paths)
    {
        var conn = Database.Open(paths.DatabasePath);
        Migrator.Migrate(conn);
        return conn;
    }

    public bool WorkerGaveUp => _host.GaveUp;

    public async Task<IReadOnlyList<BookEntry>> ListAsync() =>
        await Locked(_readLock, () =>
        {
            var tags = new TagRepository(_read).ForAllBooks();
            var places = Places();
            return (IReadOnlyList<BookEntry>)_readBooks.List()
                .Select(b => ToEntry(b, tags.GetValueOrDefault(b.Id), places.GetValueOrDefault(b.Id))).ToList();
        });

    public async Task<IReadOnlyDictionary<long, ReadingPlace>> ListPlacesAsync() => await Locked(_readLock, Places);

    public async Task<IReadOnlyList<string>> ListTagsAsync() =>
        await Locked(_readLock, () => (IReadOnlyList<string>)new TagRepository(_read).List().Select(t => t.Name).ToList());

    public async Task SetTagsAsync(long bookId, IReadOnlyList<string> tags) =>
        await Locked(_writeLock, () => { new TagRepository(_write).Set(bookId, tags); return 0; });

    public async Task AddTagsAsync(IReadOnlyList<long> bookIds, IReadOnlyList<string> tags) =>
        await Locked(_writeLock, () => { new TagRepository(_write).Add(bookIds, tags); return 0; });

    public async Task<BookEntry?> GetAsync(long bookId) =>
        await Locked(_readLock, () => _readBooks.GetById(bookId) is { } b
            ? ToEntry(b, new TagRepository(_read).ForBook(b.Id), PlaceOf(b.Id)) : null);

    public async Task<AddResult> AddAsync(string path, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => _ingest.AddAsync(path, ct), ct).ConfigureAwait(false); }   // hashing happens off the UI thread
        finally { _writeLock.Release(); }
    }

    public async Task RemoveAsync(long bookId) => await Locked(_writeLock, () => { _books.Delete(bookId); return 0; });

    public async Task<IReadOnlyList<BookProgress>> PollAsync() =>
        await Locked(_readLock, () => _disposed ? [] : _poller.Poll());   // a poll that races the shutdown just returns nothing

    public async Task<bool> GetPausedAsync() => await Locked(_writeLock, () => _control.Get().Paused);

    public async Task SetPausedAsync(bool paused) => await Locked(_writeLock, () => { _control.SetPaused(paused); return 0; });

    public async Task<int> GetCpuCapAsync() => await Locked(_writeLock, () => _control.Get().CpuCapPercent);

    public async Task SetCpuCapAsync(int percent) => await Locked(_writeLock, () => { _control.SetCpuCap(percent); return 0; });

    public async Task<WorkerStart> EnsureWorkerAsync()
    {
        if (_checkPrerequisites() is { } problem) return new WorkerStart(problem);
        await _hostLock.WaitAsync().ConfigureAwait(false);
        try { _ = _host.EnsureRunning(); }
        finally { _hostLock.Release(); }
        return new WorkerStart(null);
    }

    private BookEntry ToEntry(Book b, IReadOnlyList<string>? tags, ReadingPlace? place = null)
    {
        var job = _readJobs.Get(b.Id);
        return new BookEntry(b.Id, b.Title ?? Path.GetFileNameWithoutExtension(b.FilePath), b.Author, b.Language, b.Status,
            b.StatusReason, job?.Stage ?? IngestStage.Done, job?.Progress ?? (b.Status == BookStatus.Ready ? 1 : 0), tags ?? [],
            b.FilePath, place);
    }

    private IReadOnlyDictionary<long, ReadingPlace> Places()
    {
        var repository = new ReadingPositionRepository(_read);
        var counts = repository.SectionCounts();
        return repository.All()
            .Where(p => counts.GetValueOrDefault(p.Key) > 0)             // a book with no chapters has no place to show
            .ToDictionary(p => p.Key, p =>
            {
                var count = counts[p.Key];
                return new ReadingPlace(Math.Clamp(p.Value.SectionPos, 0, count - 1), count);
            });
    }

    /// <summary>One book's place, without counting every book's chapters (a row refresh must not scan the chunk table).</summary>
    private ReadingPlace? PlaceOf(long bookId)
    {
        var repository = new ReadingPositionRepository(_read);
        if (repository.Get(bookId) is not { } saved) return null;
        var count = repository.SectionCount(bookId);
        return count == 0 ? null : new ReadingPlace(Math.Clamp(saved.SectionPos, 0, count - 1), count);
    }

    private static async Task<T> Locked<T>(SemaphoreSlim gate, Func<T> work)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(work).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private volatile bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _host.Stop();   // the worker notices the app is gone and stops by itself (--parent-pid)
        _write.Dispose();
        _read.Dispose();
        _hostConn.Dispose();
    }

    private sealed class QueueOnly : IEmbedder, BookSearch.Core.Chunking.ITokenizer
    {
        public static readonly QueueOnly Instance = new();
        public string ModelId => "";
        public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new InvalidOperationException("The app only queues books; the background worker indexes them.");
        public IReadOnlyList<int> Count(IReadOnlyList<string> texts) =>
            throw new InvalidOperationException("The app only queues books; the background worker indexes them.");
    }
}
