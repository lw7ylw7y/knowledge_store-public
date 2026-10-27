namespace BookSearch.App.Ask;

/// <summary>
/// A small request to the chat model made on the way to a search (the follow-up rewrite, the translation). The request goes out at
/// once, so it can run beside a summary being written when the server has the room. The time limit only starts when no summary is
/// being written: while one is, the request may be waiting for the CPU and is given as long as the summary queue takes, then the
/// limit counts from the moment the queue is empty. The limit is <see cref="DefaultTimeout"/> when the chat server is already
/// running and <see cref="ColdTimeout"/> when the call has to start it (a cold load took 58 s to a first token on the owner's CPU).
/// </summary>
public static class ChatSideCall
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ColdTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The time limit for one call: <paramref name="timeout"/> if given, else by whether the chat server was running (unknown counts as running).</summary>
    public static TimeSpan LimitFor(bool? serverWasReady, TimeSpan? timeout = null) =>
        timeout ?? (serverWasReady == false ? ColdTimeout : DefaultTimeout);

    /// <param name="what">For the log line, e.g. "follow-up rewrite".</param>
    /// <param name="unavailable">The exception thrown when the model could not be used (any failure but the caller cancelling).</param>
    /// <param name="log">Receives one line when the call fails: the reason and the timing, never the question.</param>
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, string what, Func<Exception> unavailable,
        TimeSpan? timeout, Action<string>? log, Func<Task>? afterSummary, Func<bool>? chatReady, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var wasReady = chatReady?.Invoke();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var summaryIdle = afterSummary?.Invoke() ?? Task.CompletedTask;
        var summaryWasRunning = !summaryIdle.IsCompleted;
        try
        {
            var request = work(limit.Token);
            if (await Task.WhenAny(request, summaryIdle).ConfigureAwait(false) != request) limit.CancelAfter(LimitFor(wasReady, timeout));
            return await request.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // the caller cancelled
        catch (Exception e)
        {
            // Why it failed, never what was asked: the exception's type and the timing (a time limit shows as a cancellation).
            try
            {
                log?.Invoke($"note: the {what} failed after {started.ElapsedMilliseconds} ms: "
                    + (e is OperationCanceledException ? "time limit reached" : e.GetType().Name)
                    + $"; summary queue or restart work busy at the start: {(summaryWasRunning ? "yes" : "no")}"
                    + (wasReady is { } ready ? $"; chat server ready at the start: {(ready ? "yes" : "no")}" : ""));
            }
            catch (Exception) { /* a log that can't be written must never turn a handled failure into an unhandled one */ }
            throw unavailable();
        }
    }
}
