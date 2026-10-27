using BookSearch.Core.Data;
using Microsoft.Data.Sqlite;

namespace BookSearch.App.History;

/// <summary>Reads and writes the search history (ST-23). Every method is safe to call from any thread.</summary>
public interface ISearchHistoryStore
{
    /// <summary>Starts a conversation and returns its id.</summary>
    Task<long> AddThreadAsync();

    /// <summary>Adds a question; to <paramref name="threadId"/> when given.</summary>
    /// <param name="filter">The search filter the question was asked with (<see cref="BookSearch.Core.Search.SearchFilter.ToJson"/>), or null.</param>
    /// <param name="searchedAs">The follow-up rewritten to stand on its own, when it was; null when the question was searched as typed.</param>
    /// <param name="alsoSearched">The question in the other languages it was also searched in (DCR-11), or null.</param>
    Task<long> AddAsync(string question, string status, long? threadId = null, string? filter = null, string? searchedAs = null,
        IReadOnlyList<string>? alsoSearched = null);

    Task<bool> ThreadExistsAsync(long threadId);

    /// <summary>The conversations, the one with the latest question first (empty ones left out).</summary>
    Task<IReadOnlyList<SearchThread>> ListThreadsAsync();

    /// <summary>One conversation's questions, in the order they were asked.</summary>
    Task<IReadOnlyList<SearchHistoryEntry>> ListThreadAsync(long threadId);

    /// <summary>Deletes a conversation with all its questions.</summary>
    Task DeleteThreadAsync(long threadId);

    /// <summary>Sets the status and, when given, the summary text. Does nothing if the entry has been closed.</summary>
    Task UpdateAsync(long id, string status, string? summaryText = null, string? model = null, int? ms = null);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<SearchHistoryEntry>> ListAsync();

    /// <summary>Entries waiting or being written, oldest first.</summary>
    Task<IReadOnlyList<SearchHistoryEntry>> ListUnfinishedAsync();

    Task DeleteAsync(long id);
    Task ClearAsync();
}

/// <summary><see cref="SearchHistoryRepository"/> on a connection of its own, one call at a time.</summary>
public sealed class SearchHistoryStore : ISearchHistoryStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SearchHistoryRepository _repo;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SearchHistoryStore(AppPaths paths)
    {
        _conn = Database.Open(paths.DatabasePath);
        Migrator.Migrate(_conn);
        _repo = new SearchHistoryRepository(_conn);
    }

    public Task<long> AddThreadAsync() => Locked(() => _repo.AddThread());

    public Task<long> AddAsync(string question, string status, long? threadId = null, string? filter = null, string? searchedAs = null,
        IReadOnlyList<string>? alsoSearched = null) =>
        Locked(() => _repo.Add(question, status, threadId: threadId, filter: filter, searchedAs: searchedAs, alsoSearched: alsoSearched));

    public Task<bool> ThreadExistsAsync(long threadId) => Locked(() => _repo.ThreadExists(threadId));

    public async Task<IReadOnlyList<SearchThread>> ListThreadsAsync() => await Locked(() => _repo.ListThreads());

    public async Task<IReadOnlyList<SearchHistoryEntry>> ListThreadAsync(long threadId) => await Locked(() => _repo.ListThread(threadId));

    public Task DeleteThreadAsync(long threadId) => Locked(() => { _repo.DeleteThread(threadId); return 0; });

    public Task UpdateAsync(long id, string status, string? summaryText = null, string? model = null, int? ms = null) =>
        Locked(() => { _repo.Update(id, status, summaryText, model, ms); return 0; });

    public async Task<IReadOnlyList<SearchHistoryEntry>> ListAsync() => await Locked(() => _repo.List());

    public async Task<IReadOnlyList<SearchHistoryEntry>> ListUnfinishedAsync() => await Locked(() => _repo.ListUnfinished());

    public Task DeleteAsync(long id) => Locked(() => { _repo.Delete(id); return 0; });

    public Task ClearAsync() => Locked(() => { _repo.Clear(); return 0; });

    private async Task<T> Locked<T>(Func<T> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(work).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public void Dispose() => _conn.Dispose();
}
