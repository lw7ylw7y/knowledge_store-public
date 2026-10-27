using BookSearch.Core.Search;
using BookSearch.Core.Summarize;

namespace BookSearch.App.Ask;

/// <summary>Makes a follow-up question stand on its own with the chat model (DCR-6).</summary>
public interface IFollowUpRewriter
{
    /// <summary>
    /// The question rewritten so it needs no earlier question, or null when it stands on its own already or the reply was unusable
    /// (the caller then searches the question as typed). Throws <see cref="RewriteUnavailableException"/> when the model could not be
    /// used (not installed, not started, a failed request) and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    Task<string?> RewriteAsync(string question, IReadOnlyList<string> earlier, CancellationToken ct);
}

/// <summary>
/// <see cref="QuestionRewriter"/> over the chat server (started on demand by <c>SupervisedChat</c>). Any failure of the model is
/// a non-critical failure: <see cref="RewriteUnavailableException"/> tells the page to search the question as typed and say so.
/// Timing: see <see cref="ChatSideCall"/>.
/// </summary>
/// <param name="afterSummary">Completes when no summary is being written or waiting; null means none is ever running.</param>
/// <param name="chatReady">Whether the chat server is already running; only for the log line.</param>
/// <param name="log">Receives one line when the rewrite fails: the reason and the timing, never the question.</param>
public sealed class FollowUpRewriter(IChatModel chat, TimeSpan? timeout = null, Action<string>? log = null,
    Func<Task>? afterSummary = null, Func<bool>? chatReady = null) : IFollowUpRewriter
{
        public Task<string?> RewriteAsync(string question, IReadOnlyList<string> earlier, CancellationToken ct) =>
        ChatSideCall.RunAsync(token => new QuestionRewriter(chat).RewriteAsync(question, earlier, token), "follow-up rewrite",
            () => new RewriteUnavailableException(), timeout, log, afterSummary, chatReady, ct);
}

/// <summary>The language model could not rewrite the question; the question is searched as typed.</summary>
public sealed class RewriteUnavailableException() : Exception("The question could not be rewritten.");
