using BookSearch.App.Ask;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BookSearch.App.History;

/// <summary>
/// One search's summaries as they move through the queue: one summary for each result (ST-34), written one after another. Its
/// properties change on the UI thread only, so the page can be bound to it. <see cref="Search"/> is the results the summaries
/// are written from; it is null for an entry restored after a restart until its question has been searched again.
/// </summary>
public sealed partial class SummaryJob(long entryId, string question, SearchResult? search, long? threadId = null,
    string? searchedAs = null, IReadOnlyList<string>? alsoSearched = null) : ObservableObject
{
    public long EntryId { get; } = entryId;
    public string Question { get; } = question;

    /// <summary>The conversation the question belongs to.</summary>
    public long? ThreadId { get; } = threadId;

    /// <summary>The follow-up rewritten to stand on its own, or null when the question was searched as typed.</summary>
    public string? SearchedAs { get; } = searchedAs;

    /// <summary>The question in the other languages it was also searched in (DCR-11); empty when it was searched in its own language only.</summary>
    public IReadOnlyList<string> AlsoSearched { get; } = alsoSearched ?? [];

    /// <summary>The question the search was made with and the summary is written for.</summary>
    public string SummaryQuestion => SearchedAs ?? Question;
    public SearchResult? Search { get; internal set; } = search;

    [ObservableProperty] private string _status = SummaryStatus.None;
    [ObservableProperty] private SummaryPhase? _phase;
    [ObservableProperty] private SummaryRun? _run;

    /// <summary>The state of each result's summary, in the order of the results; empty when there is nothing to summarize.</summary>
    [ObservableProperty] private IReadOnlyList<PassageState> _passages = NewPassages(search);
    [ObservableProperty] private bool _modelUnavailable;
    [ObservableProperty] private string? _failure;

    // Coordinator bookkeeping.
    internal CancellationTokenSource? Cts { get; set; }
    internal bool StopRequested { get; set; }
    internal bool Discarded { get; set; }

    private static IReadOnlyList<PassageState> NewPassages(SearchResult? search) =>
        search is null || search.NotCovered ? [] : search.Hits.Select((_, i) => new PassageState(i + 1)).ToList();

    internal void Reset()
    {
        Phase = null;
        Passages = NewPassages(Search);
        Run = null;
        ModelUnavailable = false;
        Failure = null;
        StopRequested = false;
    }

    // The coordinator's updates, made on the UI thread.

    internal void PassageStarted(int index)
    {
        if (index < Passages.Count) Passages[index].Status = PassageStatus.Writing;
    }

    internal void PassageToken(int index, string token)
    {
        if (index < Passages.Count) Passages[index].Text += token;
    }

    internal void PassageDone(PassageSummary passage)
    {
        if (passage.Number > Passages.Count) return;
        var state = Passages[passage.Number - 1];
        state.Text = passage.Text;
        state.Status = passage.NotCovered ? PassageStatus.NotCovered
            : string.IsNullOrWhiteSpace(passage.Text) ? PassageStatus.Empty : PassageStatus.Written;
    }

    /// <summary>The run ended (stopped or failed): results that were not finished stay without a summary.</summary>
    internal void EndUnfinishedPassages()
    {
        foreach (var state in Passages)
            if (state.Status is PassageStatus.Waiting or PassageStatus.Writing)
            {
                state.Text = "";
                state.Status = PassageStatus.Unfinished;
            }
    }

    /// <summary>
    /// The plain text of the results summarized so far (for the history when the run was stopped or failed), or null when
    /// there is none. Called on the UI thread.
    /// </summary>
    internal string? WrittenText()
    {
        if (Search is null) return null;
        var paragraphs = new List<string>();
        for (var i = 0; i < Passages.Count && i < Search.Hits.Count; i++)
            if (Passages[i].Status is PassageStatus.Written or PassageStatus.NotCovered)
                paragraphs.Add(PassageSummary.Paragraph(SummaryPrompt.Label(Search.Hits[i]), Passages[i].Text,
                    Passages[i].Status == PassageStatus.NotCovered));
        return paragraphs.Count == 0 ? null : string.Join("\n\n", paragraphs);
    }
}
