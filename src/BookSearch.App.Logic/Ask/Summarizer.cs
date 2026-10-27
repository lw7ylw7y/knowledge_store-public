using BookSearch.Core;
using BookSearch.Core.Chunking;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using BookSearch.Models;

namespace BookSearch.App.Ask;

public enum SummaryPhase { LoadingModel, ReadingPassages, Writing }

public interface ISummarizer
{
    /// <summary>
    /// Writes a short summary of each of the search's results, one after another, reporting the phase, the result being
    /// read, each streamed fragment (with the result's index from 0) and each finished summary.
    /// Cancelling stops the model and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<SummaryRun> SummarizeAsync(string question, SearchResult search, Action<SummaryPhase> onPhase,
        Action<int> onPassageStarted, Action<int, string> onToken, Action<PassageSummary> onPassage, CancellationToken ct);

    /// <summary>
    /// Starts the language model without writing anything, so the first summary doesn't wait for it to load. Does nothing if the
    /// model is already running or can't be started (a missing file shows up when a summary is actually asked for).
    /// </summary>
    Task PrewarmAsync(CancellationToken ct);
}

/// <summary>Starts the chat server if it isn't running, then runs <see cref="SummaryService"/> against it.</summary>
public sealed class Summarizer(ServerSupervisor supervisor, IChatModel chat, ISentenceSplitter? splitter = null,
    IChunkSource? chunks = null, Func<SummaryContextLevel>? contextLevel = null) : ISummarizer
{
    private readonly ISentenceSplitter _splitter = splitter ?? SentenceSplitters.CreateDefault();

    public async Task PrewarmAsync(CancellationToken ct)
    {
        try { using var _ = await supervisor.AcquireAsync(ServerRole.Chat, ct).ConfigureAwait(false); }
        catch (ModelUnavailableException) { /* not installed, or no llama-server: reported when a summary is asked for */ }
    }

    public async Task<SummaryRun> SummarizeAsync(string question, SearchResult search, Action<SummaryPhase> onPhase,
        Action<int> onPassageStarted, Action<int, string> onToken, Action<PassageSummary> onPassage, CancellationToken ct)
    {
        // Load the model first, so "Loading model…" covers exactly the wait for the server and the next phase is only
        // the model reading the first passage. A search the library doesn't cover never loads the model.
        if (!search.NotCovered && search.Hits.Count > 0)
        {
            if (supervisor.GetState(ServerRole.Chat) != ServerState.Ready) onPhase(SummaryPhase.LoadingModel);
            using var _ = await supervisor.AcquireAsync(ServerRole.Chat, ct).ConfigureAwait(false);
        }

        // The token counts only size the passages, so an estimate is enough and needs no embedding server.
        var service = new SummaryService(chat, new ApproximateTokenizer(), _splitter, SummaryOptions.ForLevel(contextLevel?.Invoke() ?? SummaryContextLevel.Medium), chunks);
        var first = true;
        return await Task.Run(() => service.SummarizeAsync(question, search,
            index =>
            {
                first = true;
                onPhase(SummaryPhase.ReadingPassages);
                onPassageStarted(index);
            },
            (index, token) =>
            {
                if (first) { first = false; onPhase(SummaryPhase.Writing); }
                onToken(index, token);
            },
            onPassage, ct), ct).ConfigureAwait(false);
    }
}
