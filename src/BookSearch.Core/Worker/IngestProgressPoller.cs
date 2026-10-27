using BookSearch.Core.Data;
using Microsoft.Data.Sqlite;

namespace BookSearch.Core.Worker;

public sealed record BookProgress(long BookId, string Status, string Stage, double Progress, string? Error);

/// <summary>
/// Reads the worker's progress from <c>ingest_job</c> (the app calls this about once a second) and reports only the
/// books whose status, stage, progress or error changed since the last poll. Use a connection of its own.
/// </summary>
public sealed class IngestProgressPoller(SqliteConnection conn)
{
    private readonly Dictionary<long, BookProgress> _last = [];

    public IReadOnlyList<BookProgress> Poll()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT j.book_id, b.status, j.stage, j.progress, j.error
            FROM ingest_job j JOIN book b ON b.id = j.book_id
            ORDER BY j.book_id
            """;
        var changed = new List<BookProgress>();
        var seen = new HashSet<long>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var row = new BookProgress(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDouble(3),
                    r.IsDBNull(4) ? null : r.GetString(4));
                seen.Add(row.BookId);
                if (_last.TryGetValue(row.BookId, out var old) && old == row) continue;
                _last[row.BookId] = row;
                changed.Add(row);
            }
        foreach (var gone in _last.Keys.Where(id => !seen.Contains(id)).ToList()) _last.Remove(gone);   // removed books
        return changed;
    }

    public async Task RunAsync(Action<IReadOnlyList<BookProgress>> onChanged, TimeSpan? interval = null, CancellationToken ct = default)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                if (Poll() is { Count: > 0 } changed) onChanged(changed);
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* stopped */ }
    }
}
