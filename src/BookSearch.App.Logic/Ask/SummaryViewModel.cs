using BookSearch.App.History;
using BookSearch.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BookSearch.App.Ask;

/// <summary>
/// The summaries of one question of the conversation (ST-34): hands each result's summary to its row and tells the rows why one
/// is waiting or missing ("loading the model", "stopped", "could not be written"). There is no panel of its own: the summaries
/// are under the results. The writing itself is done by the <see cref="SummaryCoordinator"/>, which keeps going when the user asks
/// another question or leaves the page; Esc stops the one being written (<see cref="StopCommand"/>).
/// </summary>
public sealed partial class SummaryViewModel : ObservableObject
{
    private readonly IStrings strings;
    private readonly SummaryCoordinator coordinator;
    private SummaryJob? _job;
    private IReadOnlyList<ResultRow> _rows = [];

    public SummaryViewModel(IStrings strings, SummaryCoordinator coordinator)
    {
        this.strings = strings;
        this.coordinator = coordinator;
    }

    /// <summary>Waiting its turn or being written: Esc can stop it.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StopCommand))] private bool _isRunning;

    /// <summary>Shows the summaries of a search (or nothing, with null). The summaries of the search left behind keep being written.</summary>
    public void Attach(SummaryJob? job, IReadOnlyList<ResultRow> rows)
    {
        if (_job is not null) _job.PropertyChanged -= OnJobChanged;
        foreach (var row in _rows) row.Passage = null;
        _job = job;
        _rows = rows;
        if (job is not null) job.PropertyChanged += OnJobChanged;
        Refresh();
    }

    private void OnJobChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var job = _job;
        if (job is null)
        {
            IsRunning = false;
            return;
        }

        IsRunning = job.Status is SummaryStatus.Waiting or SummaryStatus.Writing;

        // What a result says while its summary is not there (yet): behind another question's summary, loading the model, or why it is missing.
        var waiting = job.Status == SummaryStatus.Waiting ? strings.Get("Ask_SummaryWaiting")
            : job.Status == SummaryStatus.Writing && job.Phase == Ask.SummaryPhase.LoadingModel ? strings.Get("Ask_PhaseLoading")
            : null;
        var unfinished = job.Status switch
        {
            SummaryStatus.Stopped => strings.Get("Ask_SummaryStopped"),
            SummaryStatus.Failed => job.ModelUnavailable
                ? strings.Get("Ask_SummaryUnavailable")
                : string.IsNullOrEmpty(job.Failure) ? strings.Get("History_StatusFailed") : strings.Format("Ask_SummaryFailed", job.Failure),
            _ => null,
        };

        // Each row shows its own summary once one has been asked for. While summaries are off the rows have none; after a stop or a
        // failure the finished ones stay and the unfinished ones say why (the job has already ended them).
        var show = job.Status is SummaryStatus.Waiting or SummaryStatus.Writing or SummaryStatus.Ready
            or SummaryStatus.NotCovered or SummaryStatus.Stopped or SummaryStatus.Failed;
        var reasonGiven = false;
        for (var i = 0; i < _rows.Count; i++)
        {
            var passage = show && i < job.Passages.Count ? job.Passages[i] : null;
            _rows[i].WaitingText = waiting;
            // The reason is given once, on the first result without a summary; the others just say so.
            _rows[i].UnfinishedText = unfinished is null || passage is not { Status: PassageStatus.Unfinished } ? unfinished
                : reasonGiven ? strings.Get("Ask_PassageNotSummarized") : unfinished;
            if (passage is { Status: PassageStatus.Unfinished }) reasonGiven = true;
            _rows[i].Passage = passage;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        if (_job is { } job) await coordinator.StopAsync(job);
    }
}
