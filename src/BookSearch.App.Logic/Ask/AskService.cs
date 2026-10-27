using BookSearch.Core.Data;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using BookSearch.Models;
using Microsoft.Data.Sqlite;

namespace BookSearch.App.Ask;

/// <param name="ReadyBooks">Books that are fully indexed and searchable.</param>
/// <param name="PendingBooks">Books that are still queued or being indexed.</param>
public sealed record LibrarySummary(int ReadyBooks, int PendingBooks);

/// <summary>What the filter list offers: the tags in use (with book counts) and the indexed books.</summary>
public sealed record FilterOptions(IReadOnlyList<TagInfo> Tags, IReadOnlyList<FilterBook> Books);

public interface IAskService
{
    /// <summary>The embedding server is running, so a search can use meaning as well as keywords.</summary>
    bool EmbeddingReady { get; }

    Task<LibrarySummary> GetLibrarySummaryAsync();

    /// <summary>The tags and books the user can filter by.</summary>
    Task<FilterOptions> GetFilterOptionsAsync();

    /// <summary>
    /// Searches; with <paramref name="useEmbedding"/> false it is keyword-only and never waits for a model. A non-empty
    /// <paramref name="filter"/> limits the search to the indexed books with its tags plus the books it names; if none qualifies
    /// the result says so (<see cref="SearchResult.FilterMatchedNoBooks"/>) instead of searching everything.
    /// </summary>
    /// <param name="alsoSearch">The question written in other languages (DCR-11): searched like the question itself, all results fused.</param>
    Task<SearchResult> SearchAsync(string question, bool useEmbedding, CancellationToken ct, SearchFilter? filter = null,
        IReadOnlyList<string>? alsoSearch = null);

    /// <summary>The language codes of the indexed books the search would cover, the language with the most books first (DCR-11).</summary>
    Task<IReadOnlyList<string?>> GetBookLanguagesAsync(SearchFilter filter);

    /// <summary>Starts the embedding server if needed and returns when it can answer (or throws <see cref="BookSearch.Core.ModelUnavailableException"/>).</summary>
    Task WarmUpEmbeddingAsync(CancellationToken ct);
}

/// <summary>Search for the Ask page: <see cref="SearchService"/> over library.db, with the embedding server from the supervisor.</summary>
public sealed class AskService : IAskService, IChunkSource, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);   // one SQLite connection: one search at a time
    private readonly ServerSupervisor _supervisor;
    private readonly SupervisedEmbedder _embedder;
    private readonly Func<int> _resultCount;

    /// <param name="resultCount">How many passages a search returns; read on every search, so a change in Settings applies at once.</param>
    public AskService(AppPaths paths, ServerSupervisor supervisor, Func<int>? resultCount = null)
    {
        _resultCount = resultCount ?? (() => new SearchOptions().TopN);
        _conn = Database.Open(paths.DatabasePath);
        _supervisor = supervisor;
        _embedder = new SupervisedEmbedder(supervisor);
    }

    public bool EmbeddingReady => _supervisor.GetState(ServerRole.Embedding) == ServerState.Ready;

    public async Task<LibrarySummary> GetLibrarySummaryAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var books = new BookRepository(_conn).List();
                return new LibrarySummary(
                    books.Count(b => b.Status == BookStatus.Ready),
                    books.Count(b => b.Status is BookStatus.Queued or BookStatus.Indexing));
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<FilterOptions> GetFilterOptionsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => new FilterOptions(new TagRepository(_conn).List(),
                new BookRepository(_conn).List().Where(b => b.Status == BookStatus.Ready)
                    .Select(b => new FilterBook(b.Id, b.Title ?? Path.GetFileNameWithoutExtension(b.FilePath))).ToList())).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string?>> GetBookLanguagesAsync(SearchFilter filter)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run<IReadOnlyList<string?>>(() =>
            {
                var scope = new TagRepository(_conn).Resolve(filter);
                return new BookRepository(_conn).List()
                    .Where(b => b.Status == BookStatus.Ready && (scope is null || scope.Contains(b.Id)))
                    .GroupBy(b => b.Language).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => g.Key).ToList();
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<SearchResult> SearchAsync(string question, bool useEmbedding, CancellationToken ct, SearchFilter? filter = null,
        IReadOnlyList<string>? alsoSearch = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var search = new SearchService(_conn, useEmbedding ? _embedder : null, new SearchOptions { TopN = _resultCount() });
            return await Task.Run(() =>
            {
                var books = new TagRepository(_conn).Resolve(filter ?? SearchFilter.None);
                return search.SearchAsync(question, ct, books, alsoSearch);
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The text around a result, for the summary (ST-26): read under the same lock as the searches.</summary>
    public async Task<IReadOnlyList<Chunk>> GetRunAsync(long bookId, long? sectionId, int fromIndex, int toIndex, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => new ChunkRepository(_conn).GetRun(bookId, sectionId, fromIndex, toIndex), ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task WarmUpEmbeddingAsync(CancellationToken ct)
    {
        using var lease = await _supervisor.AcquireAsync(ServerRole.Embedding, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _embedder.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _conn.Dispose();
    }
}
