using BookSearch.Core.Data;
using BookSearch.Core.Ingest;
using Microsoft.Data.Sqlite;

namespace BookSearch.Core.Worker;

public sealed record WorkerRunResult(int Indexed, int Failed);

/// <summary>
/// The background indexer's loop: takes queued books one at a time, honours the pause flag between batches, and
/// returns when nothing is left to index. A book that was started twice without ever writing a batch (it crashed
/// the process both times) is marked failed instead of being tried a third time.
/// </summary>
public sealed class IngestWorker
{
    public const int MaxAttempts = 2;

    private readonly IngestService _ingest;
    private readonly BookRepository _books;
    private readonly IngestJobRepository _jobs;
    private readonly WorkerControlRepository _control;

    public IngestWorker(SqliteConnection conn, IngestService ingest)
    {
        _ingest = ingest;
        _books = new BookRepository(conn);
        _jobs = new IngestJobRepository(conn);
        _control = new WorkerControlRepository(conn);
    }

    /// <summary>How often a paused worker looks at the pause flag again.</summary>
    public TimeSpan PausePollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public WorkerControl ReadControl() => _control.Get();

    /// <summary>Blocks while the pause flag is set. Pass this to <see cref="IngestService"/> as its between-batches hook.</summary>
    public async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        while (_control.Get().Paused)
            await Task.Delay(PausePollInterval, ct).ConfigureAwait(false);
    }

    public async Task<WorkerRunResult> RunAsync(IProgress<IngestProgress>? progress = null, CancellationToken ct = default)
    {
        int indexed = 0, failed = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_jobs.ListRunnable().FirstOrDefault() is not { } job) return new WorkerRunResult(indexed, failed);
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);

            if (job.Attempts >= MaxAttempts)
            {
                MarkFailed(job.BookId, "Indexing this book stopped the background worker twice, so it was skipped. Add it again to retry.");
                failed++;
                continue;
            }

            _jobs.BeginAttempt(job.BookId);
            try
            {
                var result = await _ingest.IngestAsync(job.BookId, progress, ct).ConfigureAwait(false);
                if (result.Status == BookStatus.Ready) indexed++;
                else if (result.Status == BookStatus.Failed) failed++;
            }
            catch (OperationCanceledException)
            {
                _jobs.ResetAttempts(job.BookId);   // a stop requested by the app is not a crash
                throw;
            }
            catch (Exception e)
            {
                MarkFailed(job.BookId, e is ModelUnavailableException ? $"The embedding model is not available: {e.Message}" : $"Indexing failed: {e.Message}");
                failed++;
            }
        }
    }

    private void MarkFailed(long bookId, string reason)
    {
        _books.SetStatus(bookId, BookStatus.Failed, reason);
        var stage = _jobs.Get(bookId)?.Stage ?? IngestStage.Extract;
        _jobs.SetStage(bookId, stage, error: reason);
        _jobs.ResetAttempts(bookId);
    }
}
