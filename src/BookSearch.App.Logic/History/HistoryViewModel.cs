using System.Collections.ObjectModel;
using System.Globalization;
using BookSearch.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BookSearch.App.History;

/// <summary>One question inside a thread on the History page: the question, when it was asked, and its summary.</summary>
public sealed partial class HistoryRow : ObservableObject
{
    private readonly IStrings _strings;

    public HistoryRow(SearchHistoryEntry entry, IStrings strings)
    {
        _strings = strings;
        Id = entry.Id;
        Question = entry.Question;
        When = entry.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        Apply(entry);
    }

    public long Id { get; }
    public string Question { get; }
    public string When { get; }

    [ObservableProperty] private string _status = SummaryStatus.None;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _summary = "";

    public bool HasStatusText => StatusText.Length > 0;
    public bool HasSummary => Summary.Length > 0;
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));
    partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(HasSummary));

    public void Apply(SearchHistoryEntry entry)
    {
        Status = entry.Status;
        Summary = entry.SummaryText ?? "";
        StatusText = entry.Status switch
        {
            SummaryStatus.Waiting => _strings.Get("History_StatusWaiting"),
            SummaryStatus.Writing => _strings.Get("History_StatusWriting"),
            SummaryStatus.Stopped => _strings.Get("History_StatusStopped"),
            SummaryStatus.Failed => _strings.Get("History_StatusFailed"),
            SummaryStatus.NotCovered => _strings.Get("History_StatusNotCovered"),
            SummaryStatus.None => _strings.Get("History_StatusNone"),
            _ => "",                                    // written: the summary itself is the content
        };
    }
}

/// <summary>One conversation in the History page: its first question as the title, the number of questions and the time of the last one.</summary>
public sealed partial class ThreadRow : ObservableObject
{
    private readonly IStrings _strings;

    public ThreadRow(SearchThread thread, IStrings strings)
    {
        _strings = strings;
        Id = thread.Id;
        Apply(thread);
    }

    public long Id { get; }
    public int Questions { get; private set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationName))] private string _title = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationName))] private string _details = "";

    /// <summary>What a screen reader says for the expander: the first question, then the question count and time.</summary>
    public string AutomationName => $"{Title}, {Details}";

    /// <summary>The questions in the order they were asked; filled when the row is expanded or reloaded.</summary>
    public ObservableCollection<HistoryRow> Entries { get; } = [];

    public void Apply(SearchThread thread)
    {
        Questions = thread.Questions;
        Title = thread.Title;
        Details = _strings.Format(thread.Questions == 1 ? "History_ThreadOne" : "History_ThreadMany",
            thread.Questions, thread.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
    }
}

/// <summary>The History page: every conversation, the one with the latest question first. Summaries fill in as they are written.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly ISearchHistoryStore _store;
    private readonly SummaryCoordinator _coordinator;
    private readonly IStrings _strings;
    private readonly IDialogs _dialogs;
    private readonly ISettingsStore _settings;
    private readonly Action<Action> _ui;
    private int _reloading;
    private bool _reloadAgain;

    /// <param name="post">Runs an action on the UI thread; by default the synchronization context of the creating thread.</param>
    public HistoryViewModel(ISearchHistoryStore store, SummaryCoordinator coordinator, IStrings strings, IDialogs dialogs,
        ISettingsStore settings, Action<Action>? post = null)
    {
        _store = store;
        _coordinator = coordinator;
        _strings = strings;
        _dialogs = dialogs;
        _settings = settings;
        var context = SynchronizationContext.Current;
        _ui = post ?? (action =>
        {
            if (context is null) action();
            else context.Post(_ => action(), null);
        });
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        coordinator.HistoryChanged += () => _ui(() => _ = ReloadSafeAsync());
    }

    /// <summary>The user chose "Continue this thread": the shell opens the Ask page with that conversation.</summary>
    public event Action<long>? ContinueRequested;

    public ObservableCollection<ThreadRow> Rows { get; } = [];
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Loads the conversations (the page calls this when it is shown).</summary>
    public async Task ReloadAsync()
    {
        // One reload at a time; a change that arrives meanwhile triggers one more, so the list never ends up stale.
        if (Interlocked.Exchange(ref _reloading, 1) == 1)
        {
            _reloadAgain = true;
            return;
        }
        try
        {
            do
            {
                _reloadAgain = false;
                var threads = await _store.ListThreadsAsync();
                var entries = (await _store.ListAsync()).Where(e => e.ThreadId is not null).ToLookup(e => e.ThreadId!.Value);
                Apply(threads, entries);
            } while (_reloadAgain);
        }
        finally { Volatile.Write(ref _reloading, 0); }
    }

    private async Task ReloadSafeAsync()
    {
        try { await ReloadAsync(); }
        catch (Exception) { /* the page is closing or the database is busy; the next change reloads */ }
    }

    /// <summary>Updates only what changed: new threads go in at their place, closed ones leave, and entries fill in.</summary>
    private void Apply(IReadOnlyList<SearchThread> threads, ILookup<long, SearchHistoryEntry> entries)
    {
        var ids = threads.Select(t => t.Id).ToHashSet();
        foreach (var gone in Rows.Where(r => !ids.Contains(r.Id)).ToList()) Rows.Remove(gone);
        for (var i = 0; i < threads.Count; i++)
        {
            var thread = threads[i];
            var row = Rows.FirstOrDefault(r => r.Id == thread.Id);
            if (row is null) Rows.Insert(Math.Min(i, Rows.Count), row = new ThreadRow(thread, _strings));
            else
            {
                row.Apply(thread);
                var at = Rows.IndexOf(row);
                if (at != i && i < Rows.Count) Rows.Move(at, i);
            }
            ApplyEntries(row, entries[thread.Id].OrderBy(e => e.Id).ToList());
        }
    }

    private void ApplyEntries(ThreadRow row, IReadOnlyList<SearchHistoryEntry> entries)
    {
        var ids = entries.Select(e => e.Id).ToHashSet();
        foreach (var gone in row.Entries.Where(r => !ids.Contains(r.Id)).ToList()) row.Entries.Remove(gone);
        for (var i = 0; i < entries.Count; i++)
        {
            var existing = row.Entries.FirstOrDefault(r => r.Id == entries[i].Id);
            if (existing is not null) existing.Apply(entries[i]);
            else row.Entries.Insert(Math.Min(i, row.Entries.Count), new HistoryRow(entries[i], _strings));
        }
    }

    /// <summary>The close button on a thread: deletes the whole conversation, after asking when it holds more than one question.</summary>
    [RelayCommand]
    private async Task CloseAsync(ThreadRow? row)
    {
        if (row is null) return;
        if (row.Questions > 1 && !await _dialogs.ConfirmDeleteThreadAsync(row.Questions)) return;
        Rows.Remove(row);                                // gone from the page at once
        if (_settings.Load().CurrentThreadId == row.Id) _settings.Save(_settings.Load() with { CurrentThreadId = null });
        await _coordinator.CloseThreadAsync(row.Id);
    }

    [RelayCommand]
    private void Continue(ThreadRow? row)
    {
        if (row is not null) ContinueRequested?.Invoke(row.Id);
    }
}
