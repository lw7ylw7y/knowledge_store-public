using System.Globalization;
using BookSearch.App;
using BookSearch.App.Ask;
using BookSearch.App.History;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using static BookSearch.Tests.SummaryKit;

namespace BookSearch.Tests;

/// <summary>The summary panel, the History page and the settings, on top of the coordinator.</summary>
public class HistoryUiTests
{
    private static (SummaryViewModel Panel, SummaryCoordinator Coordinator, InMemoryHistory History, FakeSummarizer Summarizer) Panel(
        FakeSummarizer? summarizer = null, Func<bool>? auto = null)
    {
        summarizer ??= new FakeSummarizer();
        var history = new InMemoryHistory();
        var coordinator = Coordinator(summarizer, history, auto: auto);
        return (new SummaryViewModel(new Strings(), coordinator), coordinator, history, summarizer);
    }

    // ---- the summary panel ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AskVm_SummaryPanel_ShowsStatusIncludingWaiting()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var summarizer = new FakeSummarizer
        {
            OnRun = async (q, _, onPhase, onToken, _) =>
            {
                if (q == "first")
                {
                    onPhase(SummaryPhase.LoadingModel);
                    started.SetResult();
                    await gate.Task;
                    onPhase(SummaryPhase.ReadingPassages);
                    onPhase(SummaryPhase.Writing);
                    onToken("Writing ");
                    onToken("now.");
                }
                return Run("Done. [1]");
            },
        };
        var (panel, coordinator, _, _) = Panel(summarizer);
        using var _c = coordinator;
        var first = await coordinator.RecordSearchAsync("first", Search(4));
        await started.Task;
        var second = await coordinator.RecordSearchAsync("second", Search(4));

        var loadingRows = Rows(3);
        panel.Attach(first, loadingRows);
        // The model is loading: the results not yet started say so (the real summarizer starts the first one only once it is loaded).
        Assert.All(loadingRows.Skip(1), r => Assert.Equal("Ask_PhaseLoading", r.SummaryText));
        Assert.True(panel.IsRunning);

        var waitingRows = Rows(3);
        panel.Attach(second, waitingRows);                                   // the next search's summary, waiting its turn
        Assert.All(waitingRows, r => Assert.Equal("Ask_SummaryWaiting", r.SummaryText));
        Assert.True(panel.IsRunning);

        var rows = Rows(3);
        panel.Attach(first, rows);
        gate.SetResult();
        await coordinator.WhenIdleAsync();
        Assert.False(panel.IsRunning);
        Assert.All(rows, r => Assert.Equal("Done. [1]", r.SummaryText));    // each result shows its own summary
    }

    [Fact]
    public async Task AskVm_SummaryPanel_EachResultShowsItsOwnSummaryAsItIsWritten()
    {
        var gate = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        var summarizer = new FakeSummarizer
        {
            OnRun = async (_, _, onPhase, onToken, _) =>
            {
                onPhase(SummaryPhase.Writing);
                onToken("Alice ");
                onToken("fell ");
                reached.SetResult();
                await gate.Task;
                return Run("Alice fell down.");
            },
        };
        var (panel, coordinator, _, _) = Panel(summarizer);
        using var _c = coordinator;
        var rows = Rows(3);
        var job = await coordinator.RecordSearchAsync("q", Search(4));
        panel.Attach(job, rows);
        await reached.Task;

        Assert.Equal("Alice fell ", rows[0].SummaryText);                    // being written, token by token
        Assert.Equal("Ask_PassageWaiting", rows[1].SummaryText);             // the others wait their turn
        Assert.Equal("Ask_PassageWaiting", rows[2].SummaryText);

        gate.SetResult();
        await coordinator.WhenIdleAsync();

        Assert.All(rows, r => Assert.Equal("Alice fell down.", r.SummaryText));
        Assert.All(rows, r => Assert.True(r.HasSummaryText));
    }

    [Fact]
    public async Task AskVm_SummaryPanel_AResultThatIsNotCovered_StaysAndSaysSo()
    {
        var summarizer = new FakeSummarizer
        {
            OnRun = (_, _, _, _, _) =>
            {
                var run = Run("About the topic.");
                var passages = run.Passages.Select(p => p.Number == 2 ? p with { Text = "", NotCovered = true } : p).ToList();
                return Task.FromResult(run with { Passages = passages });
            },
        };
        var (panel, coordinator, _, _) = Panel(summarizer);
        using var _c = coordinator;
        var rows = Rows(3);
        panel.Attach(await coordinator.RecordSearchAsync("q", Search(4)), rows);

        await coordinator.WhenIdleAsync();

        Assert.Equal(["About the topic.", "Ask_PassageNotCovered", "About the topic."], rows.Select(r => r.SummaryText));
        Assert.Equal(3, rows.Count);                                         // nothing was removed from the list
        Assert.Contains("Ask_PassageNotCovered", rows[1].AutomationName);    // a screen reader hears it too
    }

    [Fact]
    public async Task SummaryPanel_SummariesOff_ShowsTheResultsWithoutAnySummaryLine()
    {
        var (panel, coordinator, _, summarizer) = Panel(auto: () => false);
        using var _c = coordinator;
        var job = await coordinator.RecordSearchAsync("q", Search(4));       // automatic summaries are off
        var rows = Rows(3);

        panel.Attach(job, rows);
        await coordinator.WhenIdleAsync();

        Assert.All(rows, r => Assert.Equal("", r.SummaryText));              // no "waiting" line, nothing to press
        Assert.All(rows, r => Assert.False(r.HasSummaryText));
        Assert.False(panel.IsRunning);
        Assert.Empty(summarizer.Questions);
    }

    [Fact]
    public async Task SummaryPanel_Stop_LeavesTheUnfinishedResultsSayingWhy()
    {
        var started = new TaskCompletionSource();
        var summarizer = new FakeSummarizer
        {
            OnRun = async (_, _, _, onToken, ct) => { onToken("Partial"); started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Run("x"); },
        };
        var (panel, coordinator, _, _) = Panel(summarizer);
        using var _c = coordinator;
        var rows = Rows(3);
        panel.Attach(await coordinator.RecordSearchAsync("q", Search(4)), rows);
        await started.Task;
        Assert.True(panel.StopCommand.CanExecute(null));

        await panel.StopCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();

        Assert.False(panel.IsRunning);
        Assert.Equal("Ask_SummaryStopped", rows[0].SummaryText);            // not left looking finished, and says why
        Assert.All(rows.Skip(1), r => Assert.Equal("Ask_PassageNotSummarized", r.SummaryText));
        Assert.False(panel.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task SummaryPanel_Stop_KeepsTheFinishedSummaries_OnScreenAndInTheHistory()
    {
        var started = new TaskCompletionSource();
        var summarizer = new FakeSummarizer
        {
            FinishFirst = 1,
            OnRun = async (_, _, _, onToken, ct) => { onToken("Half of the second"); started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Run("x"); },
        };
        var (panel, coordinator, history, _) = Panel(summarizer);
        using var _c = coordinator;
        var rows = Rows(3);
        var job = await coordinator.RecordSearchAsync("q", Search(4));
        panel.Attach(job, rows);
        await started.Task;
        Assert.Equal("Written 1.", rows[0].SummaryText);                     // the first is done while the second is being written
        Assert.Equal("Half of the second", rows[1].SummaryText);

        await panel.StopCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();

        Assert.Equal("Written 1.", rows[0].SummaryText);                     // it stays
        Assert.Equal("Ask_SummaryStopped", rows[1].SummaryText);             // the half-written one does not look finished, and says why
        Assert.Equal("Ask_PassageNotSummarized", rows[2].SummaryText);       // the reason is given once
        var entry = history.Get(job.EntryId)!;
        Assert.Equal(SummaryStatus.Stopped, entry.Status);
        Assert.Equal("Book 1, Chapter: Written 1.", entry.SummaryText);      // and it is in the history too
    }

    [Fact]
    public async Task SummaryPanel_ModelFailsOnTheSecondResult_KeepsTheFirstSummary()
    {
        var summarizer = new FakeSummarizer
        {
            FinishFirst = 1,
            OnRun = (_, _, _, _, _) => throw new BookSearch.Core.ModelUnavailableException("gone"),
        };
        var (panel, coordinator, history, _) = Panel(summarizer);
        using var _c = coordinator;
        var rows = Rows(3);
        var job = await coordinator.RecordSearchAsync("q", Search(4));
        panel.Attach(job, rows);

        await coordinator.WhenIdleAsync();

        Assert.Equal("Written 1.", rows[0].SummaryText);
        Assert.Equal("Ask_SummaryUnavailable", rows[1].SummaryText);
        Assert.Equal("Book 1, Chapter: Written 1.", history.Get(job.EntryId)!.SummaryText);
        Assert.Equal(SummaryStatus.Failed, history.Get(job.EntryId)!.Status);
    }

    [Fact]
    public async Task SummaryPanel_AResultTheModelWroteNothingFor_SaysSo_AndHasNoParagraphInTheHistory()
    {
        var summarizer = new FakeSummarizer
        {
            OnRun = (_, _, _, _, _) =>
            {
                var run = Run("A fact.");
                return Task.FromResult(run with { Passages = run.Passages.Select(p => p.Number == 2 ? p with { Text = "  " } : p).ToList() });
            },
        };
        var (panel, coordinator, history, _) = Panel(summarizer);
        using var _c = coordinator;
        var rows = Rows(3);
        var job = await coordinator.RecordSearchAsync("q", Search(4));
        panel.Attach(job, rows);

        await coordinator.WhenIdleAsync();

        Assert.Equal("Ask_SummaryEmpty", rows[1].SummaryText);
        Assert.DoesNotContain("Book 2", history.Get(job.EntryId)!.SummaryText!);
        Assert.Contains("Book 3: A fact.", history.Get(job.EntryId)!.SummaryText!);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("NOT_COV")]
    [InlineData("**NOT_COV")]
    [InlineData("NOT_COVERED")]
    [InlineData("**")]
    [InlineData("  \n")]
    public void ResultRow_WhileTheReplyMayStillBeNotCovered_ShowsSummarizingNotTheMarker(string partial)
    {
        var row = new ResultRow(Hit(1), new QueryTerms(["passage"], []), new Strings());
        var state = new PassageState(1) { Status = PassageStatus.Writing, Text = partial };

        row.Passage = state;

        Assert.Equal("Ask_PassageWriting", row.SummaryText);
        state.Text = "Alice fell down";
        Assert.Equal("Alice fell down", row.SummaryText);                    // real text is shown as it comes
        Assert.Contains("Ask_PassageWriting", row.AutomationName);           // a screen reader is not given every partial word
        Assert.DoesNotContain("Alice", row.AutomationName);
    }

    [Fact]
    public async Task SummaryPanel_ClearAllHistory_DoesNotLeaveAWaitingQuestionWaitingForEver()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var summarizer = new FakeSummarizer { OnRun = async (_, _, _, _, _) => { started.TrySetResult(); await gate.Task; return Run("x"); } };
        var (panel, coordinator, _, _) = Panel(summarizer);
        using var _c = coordinator;
        await coordinator.RecordSearchAsync("first", Search(4));
        await started.Task;
        var waiting = await coordinator.RecordSearchAsync("second", Search(4));      // queued behind the first
        var rows = Rows(3);
        panel.Attach(waiting, rows);
        Assert.True(panel.IsRunning);
        Assert.All(rows, r => Assert.Equal("Ask_SummaryWaiting", r.SummaryText));

        await coordinator.ClearAsync();
        gate.SetResult();
        await coordinator.WhenIdleAsync();

        Assert.False(panel.IsRunning);                                       // Esc has nothing left to stop here
        Assert.Equal("Ask_SummaryStopped", rows[0].SummaryText);
    }

    [Fact]
    public async Task SummaryPanel_FailuresAreShownInTheResults()
    {
        var summarizer = new FakeSummarizer
        {
            OnRun = (q, _, _, _, _) => q == "no model" ? throw new BookSearch.Core.ModelUnavailableException("x") : throw new InvalidOperationException("boom"),
        };
        var (panel, coordinator, _, _) = Panel(summarizer);
        using var _c = coordinator;

        var noModel = Rows(3);
        panel.Attach(await coordinator.RecordSearchAsync("no model", Search(4)), noModel);
        await coordinator.WhenIdleAsync();
        Assert.Equal("Ask_SummaryUnavailable", noModel[0].SummaryText);      // the long reason once...
        Assert.All(noModel.Skip(1), r => Assert.Equal("Ask_PassageNotSummarized", r.SummaryText));   // ...then a short line

        var broken = Rows(3);
        panel.Attach(await coordinator.RecordSearchAsync("broken", Search(4)), broken);
        await coordinator.WhenIdleAsync();
        Assert.Equal("Ask_SummaryFailed", broken[0].SummaryText);

        var unrelated = Rows(2);
        panel.Attach(await coordinator.RecordSearchAsync("unrelated", Search(2, notCovered: true)), unrelated);
        Assert.All(unrelated, r => Assert.Equal("", r.SummaryText));         // the library doesn't cover it: the page's banner says so
    }

    // ---- the Ask page's view model ----------------------------------------------------------------------------------

    private static AskViewModel Ask(FakeAsk ask, SummaryCoordinator coordinator, string question = "why did she fall") =>
        new(ask, new Strings(), coordinator, new Settings(), new InMemoryHistory()) { Question = question };

    [Fact]
    public async Task AskVm_ShowingThePage_LoadsTheModelInTheBackground()
    {
        var summarizer = new FakeSummarizer();
        using var coordinator = Coordinator(summarizer, new InMemoryHistory());
        var vm = Ask(new FakeAsk(), coordinator);

        await vm.RefreshAsync();                                             // what the page does when it is shown

        await SummaryKit.Eventually(() => summarizer.Prewarms >= 1);
    }

    [Fact]
    public async Task AskVm_ContinueThread_OnTheThreadAlreadyOnScreen_KeepsItsLiveResults()
    {
        var history = new InMemoryHistory();
        using var coordinator = Coordinator(new FakeSummarizer(), history);
        var vm = new AskViewModel(new FakeAsk(), new Strings(), coordinator, new Settings(), history, a => a()) { Question = "why did she fall" };
        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();
        var turn = vm.Turns[0];
        var results = turn.Results.ToList();

        await vm.ShowThreadAsync(vm.ThreadId!.Value);                         // History > "Continue this thread" on the active thread

        Assert.Same(turn, vm.Turns[0]);                                      // not rebuilt from the saved text
        Assert.True(turn.IsLive);
        Assert.Equal(results, turn.Results);
    }

    [Fact]
    public async Task AskVm_AnEarlierQuestion_KeepsItsResultsAndSummariesOpen()
    {
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory());
        var vm = Ask(new FakeAsk(), coordinator);
        await vm.SearchCommand.ExecuteAsync(null);
        var first = vm.Turns[0];

        vm.Question = "and then?";
        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();

        Assert.True(first.SourcesExpanded);                                  // the summaries are in the results: they stay visible
        Assert.All(first.Results, r => Assert.True(r.HasSummaryText));
    }

    [Fact]
    public async Task AskVm_FinalSearch_IsRecordedAndItsSummaryQueued()
    {
        var history = new InMemoryHistory();
        var summarizer = new FakeSummarizer();
        using var coordinator = Coordinator(summarizer, history);
        var vm = Ask(new FakeAsk(), coordinator);

        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();

        var entry = Assert.Single(history.All());
        Assert.Equal("why did she fall", entry.Question);
        Assert.Equal(SummaryStatus.Ready, entry.Status);
        Assert.All(vm.Latest!.Results, r => Assert.True(r.HasSummaryText));
        Assert.Equal(["why did she fall"], summarizer.Questions);
    }

    [Fact]
    public async Task AskVm_KeywordFirstThenMeaning_RecordsTheSearchOnlyOnceFinal()
    {
        var history = new InMemoryHistory();
        var release = new TaskCompletionSource();
        var ask = new FakeAsk
        {
            Ready = false,
            OnWarmUp = async _ => await release.Task,
            OnSearch = (_, embedding, _) => Task.FromResult(embedding ? Search(5) : Search(2) with { KeywordOnly = true }),
        };
        var summarizer = new FakeSummarizer();
        using var coordinator = Coordinator(summarizer, history, ask);
        var vm = Ask(ask, coordinator);

        var run = vm.SearchCommand.ExecuteAsync(null);
        await SummaryKit.Eventually(() => vm.Latest!.HasWarning);                    // the keyword results are on screen
        Assert.Empty(history.All());                                         // but the search is not final yet, so nothing is recorded
        Assert.Empty(summarizer.Questions);

        release.SetResult();
        await run;
        await coordinator.WhenIdleAsync();

        Assert.Single(history.All());
        Assert.Equal(5, vm.Latest!.Results.Count);                                   // the summary is written from the final results
    }

    [Fact]
    public async Task AskVm_MeaningSearchUnavailable_KeywordResultsAreFinal()
    {
        var history = new InMemoryHistory();
        var ask = new FakeAsk
        {
            Ready = false,
            OnWarmUp = _ => throw new BookSearch.Core.ModelUnavailableException("no server"),
            OnSearch = (_, _, _) => Task.FromResult(Search(2) with { KeywordOnly = true }),
        };
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);
        var vm = Ask(ask, coordinator);

        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();

        var entry = Assert.Single(history.All());                            // recorded, and summarized from what there is
        Assert.Equal(SummaryStatus.Ready, entry.Status);
        Assert.True(vm.Latest!.HasWarning);
    }

    [Fact]
    public async Task AskVm_NewSearchWhileSummarizing_LetsTheFirstFinishAndShowsTheNewOne()
    {
        var history = new InMemoryHistory();
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var summarizer = new FakeSummarizer
        {
            OnRun = async (q, _, _, _, _) =>
            {
                if (q == "first question") { started.SetResult(); await gate.Task; }
                return Run($"About {q}. [1]");
            },
        };
        using var coordinator = Coordinator(summarizer, history);
        var vm = Ask(new FakeAsk(), coordinator, "first question");
        await vm.SearchCommand.ExecuteAsync(null);
        await started.Task;
        var firstJob = vm.Latest!.Summary;

        vm.Question = "second question";
        await vm.SearchCommand.ExecuteAsync(null);                           // a new search while the first summary is written

        Assert.All(vm.Latest!.Results, r => Assert.Equal("Ask_SummaryWaiting", r.SummaryText));   // the new search waits its turn
        gate.SetResult();
        await coordinator.WhenIdleAsync();

        Assert.Equal(["first question", "second question"], summarizer.Questions);
        Assert.All(history.All(), e => Assert.Equal(SummaryStatus.Ready, e.Status));   // the first one was finished, not cancelled
        Assert.All(vm.Latest!.Results, r => Assert.True(r.HasSummaryText));
        _ = firstJob;
    }

    [Fact]
    public async Task Thread_NewQuestion_JoinsTheCurrentThread()
    {
        var history = new InMemoryHistory();
        using var coordinator = Coordinator(new FakeSummarizer(), history);
        var vm = Ask(new FakeAsk(), coordinator, "who is Alice");

        await vm.SearchCommand.ExecuteAsync(null);
        vm.Question = "and her sister?";
        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();

        Assert.Equal(["who is Alice", "and her sister?"], vm.Turns.Select(t => t.Question));
        var entries = history.All().OrderBy(e => e.Id).ToList();
        Assert.Equal(2, entries.Count);
        Assert.NotNull(entries[0].ThreadId);
        Assert.Equal(entries[0].ThreadId, entries[1].ThreadId);                // one thread
        Assert.Equal(entries[0].ThreadId, vm.ThreadId);
        Assert.Equal("", vm.Question);                                         // the box is emptied
    }

    [Fact]
    public async Task Thread_NewThread_StartsAFreshOneAndKeepsTheOld()
    {
        var history = new InMemoryHistory();
        var settings = new Settings();
        using var coordinator = Coordinator(new FakeSummarizer(), history);
        var vm = new AskViewModel(new FakeAsk(), new Strings(), coordinator, settings, history, a => a()) { Question = "first topic" };
        await vm.SearchCommand.ExecuteAsync(null);
        var oldThread = vm.ThreadId;

        vm.NewThreadCommand.Execute(null);

        Assert.Empty(vm.Turns);
        Assert.Null(vm.ThreadId);
        Assert.Null(settings.Load().CurrentThreadId);                          // the next start is blank
        Assert.Single(history.All());                                           // the old question is still saved
        vm.Question = "second topic";
        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();
        Assert.NotEqual(oldThread, vm.ThreadId);
        Assert.Equal(2, history.All().Select(e => e.ThreadId).Distinct().Count());
    }

    [Fact]
    public async Task Thread_Restore_ShowsPastQuestionsAsTextOnly()
    {
        var history = new InMemoryHistory();
        var settings = new Settings();
        var thread = history.AddThread();
        var first = history.Add("who is Alice", SummaryStatus.Ready, threadId: thread);
        await history.UpdateAsync(first, SummaryStatus.Ready, "Alice is a girl.");
        history.Add("and the rabbit?", SummaryStatus.NotCovered, threadId: thread);
        settings.Save(settings.Load() with { CurrentThreadId = thread });
        var ask = new FakeAsk();
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);
        var vm = new AskViewModel(ask, new Strings(), coordinator, settings, history, a => a());

        await vm.RestoreAsync();

        Assert.Equal(["who is Alice", "and the rabbit?"], vm.Turns.Select(t => t.Question));
        Assert.All(vm.Turns, t => { Assert.True(t.IsRestored); Assert.Empty(t.Results); Assert.False(t.IsLive); });
        Assert.Equal("Alice is a girl.", vm.Turns[0].SavedSummary);
        Assert.Equal("History_StatusNotCovered", vm.Turns[1].SavedStatus);
        Assert.Empty(ask.Searches);                                             // nothing is searched again
        Assert.Equal(thread, vm.ThreadId);
    }

    [Fact]
    public async Task Thread_RestoreWithNothingSaved_StaysBlank()
    {
        var history = new InMemoryHistory();
        using var coordinator = Coordinator(new FakeSummarizer(), history);
        var vm = new AskViewModel(new FakeAsk(), new Strings(), coordinator, new Settings(), history, a => a());

        await vm.RestoreAsync();

        Assert.Empty(vm.Turns);
    }

    [Fact]
    public async Task Coordinator_RestartCompletion_UpdatesTheRestoredTurn()
    {
        var history = new InMemoryHistory();
        var settings = new Settings();
        var thread = history.AddThread();
        history.Add("who is Alice", SummaryStatus.Waiting, threadId: thread);
        settings.Save(settings.Load() with { CurrentThreadId = thread });
        var ask = new FakeAsk();
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);
        var vm = new AskViewModel(ask, new Strings(), coordinator, settings, history, a => a());
        await vm.RestoreAsync();
        Assert.True(vm.Turns[0].IsUnfinished);

        await coordinator.ResumeUnfinishedAsync();
        await coordinator.WhenIdleAsync();
        await SummaryKit.Eventually(() => vm.Turns[0].SavedSummary == "Book 1: A fact.");

        Assert.False(vm.Turns[0].IsUnfinished);
    }

    private static AskViewModel AskWithRewriter(FakeAsk ask, SummaryCoordinator coordinator, FakeRewriter rewriter,
        string question = "Who is Elizabeth Bennet in the novel", int contextQuestions = 1, InMemoryHistory? history = null) =>
        new(ask, new Strings(), coordinator, new Settings { Current = new AppSettings { ContextQuestions = contextQuestions } },
            history ?? new InMemoryHistory(), rewriter: rewriter) { Question = question };

    private static async Task AskAll(AskViewModel vm, params string[] questions)
    {
        foreach (var q in questions)
        {
            vm.Question = q;
            await vm.SearchCommand.ExecuteAsync(null);
        }
    }

    [Fact]
    public async Task FollowUp_IsRewritten_AndOnlyTheRewrittenQuestionIsSearchedAndSummarized()
    {
        var ask = new FakeAsk();
        var summarizer = new FakeSummarizer();
        var history = new InMemoryHistory();
        var rewriter = new FakeRewriter { OnRewrite = (_, _) => "Who is Elizabeth Bennet's sister?" };
        using var coordinator = Coordinator(summarizer, history, ask);
        var vm = AskWithRewriter(ask, coordinator, rewriter, history: history);
        await vm.SearchCommand.ExecuteAsync(null);

        await AskAll(vm, "and her sister?");
        await coordinator.WhenIdleAsync();

        Assert.Single(rewriter.Calls);                                             // the first question stands alone: no model call
        Assert.Equal("and her sister?", rewriter.Calls[0].Question);
        Assert.Equal(["Who is Elizabeth Bennet in the novel"], rewriter.Calls[0].Earlier);
        Assert.Equal(["Who is Elizabeth Bennet in the novel", "Who is Elizabeth Bennet's sister?"], ask.Searches.Select(x => x.Question));
        Assert.Equal(["Who is Elizabeth Bennet in the novel", "Who is Elizabeth Bennet's sister?"], summarizer.Questions);
        Assert.Equal("and her sister?", vm.Latest!.Question);                       // the page still shows what was typed
        Assert.True(vm.Latest.HasContext);
        Assert.Equal("Who is Elizabeth Bennet's sister?", vm.Latest.ContextQuestion);
        Assert.Equal("Ask_SearchedAs", vm.Latest.ContextText);
        Assert.Equal([null, "Who is Elizabeth Bennet's sister?"], history.All().Select(e => e.SearchedAs));
    }

    [Fact]
    public async Task FollowUp_LongSelfContained_IsSearchedAsTyped_WithoutAModelCall()
    {
        var ask = new FakeAsk();
        var rewriter = new FakeRewriter();
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = AskWithRewriter(ask, coordinator, rewriter);

        await AskAll(vm, "Who is Elizabeth Bennet in the novel", "What happens to Captain Ahab at the end of the voyage");

        Assert.Empty(rewriter.Calls);
        Assert.False(vm.Latest!.HasContext);
    }

    [Fact]
    public async Task FollowUp_TheModelSaysItStandsAlone_IsSearchedAsTyped()
    {
        var ask = new FakeAsk();
        var rewriter = new FakeRewriter { OnRewrite = (_, _) => null };
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = AskWithRewriter(ask, coordinator, rewriter);

        await AskAll(vm, "Who is Elizabeth Bennet in the novel", "Who is Darcy to her?");

        Assert.Single(rewriter.Calls);
        Assert.Equal("Who is Darcy to her?", ask.Searches[1].Question);
        Assert.False(vm.Latest!.HasContext);
        Assert.Null(vm.Latest.Warning);
    }

    [Fact]
    public async Task FollowUp_ContextChipRemoved_SearchesTheFollowUpAsTyped()
    {
        var ask = new FakeAsk();
        var rewriter = new FakeRewriter();
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = AskWithRewriter(ask, coordinator, rewriter);
        await AskAll(vm, "Who is Elizabeth Bennet in the novel", "and her sister?");
        Assert.True(vm.Latest!.HasContext);
        Assert.Equal(2, vm.Turns.Count);

        vm.Latest!.RemoveContextCommand.Execute(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 3 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.Equal("and her sister?", ask.Searches[2].Question);                  // as typed, no rewrite
        Assert.Single(rewriter.Calls);                                             // the model was not asked again
        Assert.False(vm.Latest!.HasContext);
        Assert.Equal(["Who is Elizabeth Bennet in the novel", "and her sister?"], vm.Turns.Select(t => t.Question));   // replaced, not added
    }

    [Fact]
    public async Task FollowUp_TheModelIsUnavailable_IsSearchedAsTyped_AndThePageSaysSo()
    {
        var ask = new FakeAsk();
        var summarizer = new FakeSummarizer();
        var rewriter = new FakeRewriter { OnRewrite = (_, _) => throw new RewriteUnavailableException() };
        using var coordinator = Coordinator(summarizer, new InMemoryHistory(), ask);
        var vm = AskWithRewriter(ask, coordinator, rewriter);

        await AskAll(vm, "Who is Elizabeth Bennet in the novel", "and her sister?");
        await coordinator.WhenIdleAsync();

        Assert.Equal("and her sister?", ask.Searches[1].Question);
        Assert.Equal("and her sister?", summarizer.Questions[1]);
        Assert.Equal("Ask_WarningNoRewrite", vm.Latest!.Warning);
        Assert.False(vm.Latest.HasContext);
        Assert.Equal(2, vm.Turns.Count);                                           // the search went on
    }

    [Fact]
    public async Task FollowUp_AChainOfFollowUps_GivesTheRewriterTheEarlierQuestionAsItWasSearched()
    {
        var ask = new FakeAsk();
        var rewriter = new FakeRewriter { OnRewrite = (q, _) => q == "and her sister?" ? "Who is Elizabeth Bennet's sister?" : "How old is Jane Bennet?" };
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = AskWithRewriter(ask, coordinator, rewriter);
        await vm.SearchCommand.ExecuteAsync(null);

        await AskAll(vm, "and her sister?", "how old is she?");

        Assert.Equal(["Who is Elizabeth Bennet's sister?"], rewriter.Calls[1].Earlier);   // not "and her sister?", which has no name in it
    }

    [Fact]
    public async Task FollowUp_AutoSummaryOff_IsSearchedAsTyped_WithoutLoadingTheModel()
    {
        var ask = new FakeAsk();
        var rewriter = new FakeRewriter();
        var settings = new Settings(auto: false);
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask, () => false);
        var vm = new AskViewModel(ask, new Strings(), coordinator, settings, new InMemoryHistory(), rewriter: rewriter)
            { Question = "Who is Elizabeth Bennet in the novel" };
        await vm.SearchCommand.ExecuteAsync(null);

        await AskAll(vm, "and her sister?");

        Assert.Empty(rewriter.Calls);
        Assert.Equal("and her sister?", ask.Searches[1].Question);
        Assert.False(vm.Latest!.HasContext);
        Assert.Null(vm.Latest.Warning);
    }

    [Fact]
    public async Task FollowUp_AfterARestart_TheTurnShowsWhatWasSearched()
    {
        var history = new InMemoryHistory();
        var settings = new Settings();
        var thread = history.AddThread();
        history.Add("Who is Elizabeth Bennet", SummaryStatus.Ready, threadId: thread);
        history.Add("and her sister?", SummaryStatus.Ready, threadId: thread, searchedAs: "Who is Elizabeth Bennet's sister?");
        settings.Save(settings.Load() with { CurrentThreadId = thread });
        var ask = new FakeAsk();
        var rewriter = new FakeRewriter();
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);
        var vm = new AskViewModel(ask, new Strings(), coordinator, settings, history, a => a(), rewriter: rewriter);

        await vm.RestoreAsync();

        Assert.False(vm.Turns[0].HasContext);
        Assert.True(vm.Turns[1].HasContext);
        Assert.Equal("Who is Elizabeth Bennet's sister?", vm.Turns[1].ContextQuestion);

        vm.Question = "how old is she?";                                             // a new follow-up builds on the searched question
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(["Who is Elizabeth Bennet's sister?"], rewriter.Calls[0].Earlier);
    }

    [Fact]
    public async Task FollowUp_RewriteAndEmbeddingBothUnavailable_ShowBothWarnings()
    {
        var ask = new FakeAsk
        {
            OnSearch = (_, _, _) => Task.FromResult(Search(3) with { KeywordOnly = true }),
        };
        var rewriter = new FakeRewriter { OnRewrite = (_, _) => throw new RewriteUnavailableException() };
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = AskWithRewriter(ask, coordinator, rewriter);
        await vm.SearchCommand.ExecuteAsync(null);

        await AskAll(vm, "and her sister?");

        Assert.Equal("Ask_WarningUnavailable Ask_WarningNoRewrite", vm.Latest!.Warning);
    }

    // ---- how many earlier questions the rewriter sees (Settings, 0 to 3) ---------------------------------------------------

    [Theory]
    [InlineData(1, new[] { "Who is Mr Darcy" })]
    [InlineData(2, new[] { "Who is Jane Bennet", "Who is Mr Darcy" })]
    [InlineData(3, new[] { "Who is Elizabeth Bennet", "Who is Jane Bennet", "Who is Mr Darcy" })]
    public async Task ContextSetting_TheRewriterSeesTheLastNQuestions(int count, string[] expected)
    {
        var ask = new FakeAsk();
        var rewriter = new FakeRewriter { OnRewrite = (_, _) => null };   // every question stands alone, so earlier ones are as typed
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = AskWithRewriter(ask, coordinator, rewriter, "Who is Elizabeth Bennet", count);
        await vm.SearchCommand.ExecuteAsync(null);

        await AskAll(vm, "Who is Jane Bennet", "Who is Mr Darcy", "and his sister?");

        Assert.Equal(expected, rewriter.Calls.Last().Earlier);
    }

    [Fact]
    public async Task ContextSetting_Zero_SearchesEveryQuestionAsTyped()
    {
        var ask = new FakeAsk();
        var rewriter = new FakeRewriter();
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = AskWithRewriter(ask, coordinator, rewriter, contextQuestions: 0);
        await vm.SearchCommand.ExecuteAsync(null);

        await AskAll(vm, "and her sister?");

        Assert.Empty(rewriter.Calls);
        Assert.Equal("and her sister?", ask.Searches[1].Question);
        Assert.False(vm.Latest!.HasContext);
    }

    [Fact]
    public async Task ContextSetting_AfterARestart_AnUnfinishedFollowUpIsSearchedAsItWasRewritten()
    {
        var history = new InMemoryHistory();
        var thread = history.AddThread();
        history.Add("Who is Elizabeth Bennet", SummaryStatus.Ready, threadId: thread);
        history.Add("and her sister?", SummaryStatus.Waiting, threadId: thread, searchedAs: "Who is Elizabeth Bennet's sister?");
        var summarizer = new FakeSummarizer();
        var ask = new FakeAsk();
        using var coordinator = Coordinator(summarizer, history, ask);

        await coordinator.ResumeUnfinishedAsync();
        await coordinator.WhenIdleAsync();

        Assert.Equal(["Who is Elizabeth Bennet's sister?"], ask.Searches.Select(x => x.Question));   // no new model call is needed
        Assert.Equal(["Who is Elizabeth Bennet's sister?"], summarizer.Questions);
    }

    [Fact]
    public async Task Coordinator_QuestionsInAThread_QueueOneAtATime()
    {
        var summarizer = new FakeSummarizer();
        using var coordinator = Coordinator(summarizer, new InMemoryHistory());
        var first = await coordinator.RecordSearchAsync("one", Search(3));
        await coordinator.RecordSearchAsync("two", Search(3), first.ThreadId);
        await coordinator.RecordSearchAsync("three", Search(3), first.ThreadId);
        await coordinator.WhenIdleAsync();

        Assert.Equal(["one", "two", "three"], summarizer.Questions);
        Assert.Equal(1, summarizer.MaxRunning);
    }

    // ---- the History page ---------------------------------------------------------------------------------------------

    private static HistoryViewModel HistoryVm(InMemoryHistory history, SummaryCoordinator coordinator, Dialogs? dialogs = null,
        Settings? settings = null) =>
        new(history, coordinator, new Strings(), dialogs ?? new Dialogs(), settings ?? new Settings(), post: a => a());

    private static long Thread(InMemoryHistory history, params (string Question, string Status, DateTime At)[] questions)
    {
        var thread = history.AddThread();
        foreach (var (question, status, at) in questions) history.Add(question, status, at, thread);
        return thread;
    }

    [Fact]
    public async Task History_ListsThreadsWithTitleCountAndLastActivity()
    {
        var history = new InMemoryHistory();
        var march1 = new DateTime(2026, 3, 1, 8, 30, 0, DateTimeKind.Utc);
        var march2 = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);
        var older = Thread(history, ("older first", SummaryStatus.Ready, march1), ("older second", SummaryStatus.Ready, march1.AddMinutes(5)));
        await history.UpdateAsync(1, SummaryStatus.Ready, "Old summary.");
        Thread(history, ("newer", SummaryStatus.NotCovered, march2));
        using var coordinator = Coordinator(new FakeSummarizer(), history);
        var vm = HistoryVm(history, coordinator);

        await vm.ReloadAsync();

        Assert.Equal(["newer", "older first"], vm.Rows.Select(r => r.Title));          // the first question is the title
        Assert.Equal(older, vm.Rows[1].Id);
        Assert.Equal(2, vm.Rows[1].Questions);
        Assert.Equal("History_ThreadOne", vm.Rows[0].Details.Length > 0 ? "History_ThreadOne" : "");
        Assert.Equal(["older first", "older second"], vm.Rows[1].Entries.Select(e => e.Question));   // in the order asked
        Assert.Equal(march1.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), vm.Rows[1].Entries[0].When);
        Assert.Equal("Old summary.", vm.Rows[1].Entries[0].Summary);
        Assert.False(vm.Rows[1].Entries[0].HasStatusText);                    // a written summary needs no status line
        Assert.Equal("History_StatusNotCovered", vm.Rows[0].Entries[0].StatusText);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task HistoryVm_Empty_SaysSo()
    {
        var history = new InMemoryHistory();
        using var coordinator = Coordinator(new FakeSummarizer(), history);
        var vm = HistoryVm(history, coordinator);

        await vm.ReloadAsync();

        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task HistoryVm_EntryFillsInAsTheSummaryIsWritten_WithoutRebuildingTheList()
    {
        var history = new InMemoryHistory();
        var gate = new TaskCompletionSource();
        var summarizer = new FakeSummarizer { OnRun = async (_, _, _, _, _) => { await gate.Task; return Run("Finished text.", passages: 1); } };
        using var coordinator = Coordinator(summarizer, history);
        var vm = HistoryVm(history, coordinator);
        await vm.ReloadAsync();

        await coordinator.RecordSearchAsync("live", Search(4));
        await SummaryKit.Eventually(() => vm.Rows.Count == 1 && vm.Rows[0].Entries.Count == 1);
        var row = vm.Rows[0];
        var entry = row.Entries[0];
        await SummaryKit.Eventually(() => entry.Status == SummaryStatus.Writing);
        Assert.Equal("History_StatusWriting", entry.StatusText);

        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedEventArgs>();
        vm.Rows.CollectionChanged += (_, e) => changes.Add(e);
        row.Entries.CollectionChanged += (_, e) => changes.Add(e);
        gate.SetResult();
        await coordinator.WhenIdleAsync();
        await SummaryKit.Eventually(() => entry.Status == SummaryStatus.Ready);

        Assert.Equal("Book 1: Finished text.", entry.Summary);
        Assert.Same(row, vm.Rows[0]);                                        // the same row objects were updated
        Assert.Same(entry, row.Entries[0]);
        Assert.Empty(changes);                                               // and the lists themselves were not rebuilt
    }

    [Fact]
    public async Task Thread_Close_DeletesAllItsQuestions()
    {
        var history = new InMemoryHistory();
        var at = DateTime.UtcNow;
        Thread(history, ("keep", SummaryStatus.Ready, at));
        var doomed = Thread(history, ("close me", SummaryStatus.Ready, at), ("and me", SummaryStatus.Ready, at));
        var settings = new Settings();
        settings.Save(settings.Load() with { CurrentThreadId = doomed });
        var dialogs = new Dialogs();
        using var coordinator = Coordinator(new FakeSummarizer(), history);
        var vm = HistoryVm(history, coordinator, dialogs, settings);
        await vm.ReloadAsync();

        await vm.CloseCommand.ExecuteAsync(vm.Rows.Single(r => r.Id == doomed));

        Assert.Equal([2], dialogs.ThreadDeletesAsked);                        // a thread of two questions asks first
        Assert.Equal(["keep"], vm.Rows.Select(r => r.Title));
        Assert.Equal(["keep"], history.All().Select(e => e.Question));
        Assert.Null(settings.Load().CurrentThreadId);                          // the page no longer restores it
    }

    [Fact]
    public async Task Thread_Close_OfOneQuestion_DoesNotAsk_AndDeclinedDeletesNothing()
    {
        var history = new InMemoryHistory();
        var at = DateTime.UtcNow;
        var single = Thread(history, ("alone", SummaryStatus.Ready, at));
        var pair = Thread(history, ("a", SummaryStatus.Ready, at), ("b", SummaryStatus.Ready, at));
        var dialogs = new Dialogs(answer: false);
        using var coordinator = Coordinator(new FakeSummarizer(), history);
        var vm = HistoryVm(history, coordinator, dialogs);
        await vm.ReloadAsync();

        await vm.CloseCommand.ExecuteAsync(vm.Rows.Single(r => r.Id == pair));
        Assert.Equal(3, history.All().Count);                                  // declined: nothing deleted

        await vm.CloseCommand.ExecuteAsync(vm.Rows.Single(r => r.Id == single));
        Assert.Equal([2], dialogs.ThreadDeletesAsked);                        // no question for a single one
        Assert.Equal(2, history.All().Count);
    }

    [Fact]
    public async Task Thread_ClearAllHistory_RemovesEveryThread()
    {
        var history = new InMemoryHistory();
        Thread(history, ("x", SummaryStatus.Ready, DateTime.UtcNow));
        Thread(history, ("y", SummaryStatus.Ready, DateTime.UtcNow));
        using var coordinator = Coordinator(new FakeSummarizer(), history);

        await coordinator.ClearAsync();

        Assert.Empty(await history.ListThreadsAsync());
        Assert.Empty(history.All());
    }

    [Fact]
    public async Task History_ContinueThread_OpensItInAsk()
    {
        var history = new InMemoryHistory();
        var thread = Thread(history, ("what is time", SummaryStatus.Ready, DateTime.UtcNow));
        using var coordinator = Coordinator(new FakeSummarizer(), history);
        var historyVm = HistoryVm(history, coordinator);
        await historyVm.ReloadAsync();
        long? asked = null;
        historyVm.ContinueRequested += id => asked = id;

        historyVm.ContinueCommand.Execute(historyVm.Rows[0]);

        Assert.Equal(thread, asked);
        var ask = new FakeAsk();
        var askVm = new AskViewModel(ask, new Strings(), coordinator, new Settings(), history, a => a());
        await askVm.ShowThreadAsync(asked!.Value);
        Assert.Equal(["what is time"], askVm.Turns.Select(t => t.Question));
        Assert.Equal(thread, askVm.ThreadId);
        Assert.Empty(ask.Searches);
    }

    [Fact]
    public async Task History_OpenEntry_DoesNotRunSearchOrModel()
    {
        var history = new InMemoryHistory();
        var id = history.Add("old question", SummaryStatus.Ready, threadId: history.AddThread());
        await history.UpdateAsync(id, SummaryStatus.Ready, "Old summary.");
        var summarizer = new FakeSummarizer();
        var ask = new FakeAsk();
        using var coordinator = Coordinator(summarizer, history, ask);
        var vm = HistoryVm(history, coordinator);

        await vm.ReloadAsync();                                              // opening the page and reading the entry

        Assert.Equal("Old summary.", vm.Rows[0].Entries[0].Summary);
        Assert.Empty(ask.Searches);
        Assert.Empty(summarizer.Questions);
    }

    // ---- settings -------------------------------------------------------------------------------------------------------

    private static SettingsViewModel Settings(Settings store, SummaryCoordinator coordinator, Dialogs dialogs) =>
        new(new AppPaths("/tmp/x"), store, BookSearch.Models.Management.ModelTier.Minimum, new LibraryStatus(0, "v0.1.9", 3),
            new Strings(), coordinator, dialogs);

    [Fact]
    public void Settings_AutoSummaryOff_PersistsAndAppliesToNextSearch()
    {
        var store = new Settings();
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), auto: () => store.Load().AutoSummary);
        var vm = Settings(store, coordinator, new Dialogs());
        Assert.True(vm.AutoSummary);

        vm.AutoSummary = false;

        Assert.False(store.Load().AutoSummary);
        var job = coordinator.RecordSearchAsync("q", Search(4)).GetAwaiter().GetResult();
        Assert.Equal(SummaryStatus.None, job.Status);                        // the next search is recorded without a summary
    }

    [Fact]
    public async Task Settings_ClearHistory_RequiresConfirmation()
    {
        var history = new InMemoryHistory();
        history.Add("one", SummaryStatus.Ready);
        history.Add("two", SummaryStatus.Ready);
        using var coordinator = Coordinator(new FakeSummarizer(), history);

        var declined = new Dialogs(answer: false);
        await Settings(new Settings(), coordinator, declined).ClearHistoryCommand.ExecuteAsync(null);
        Assert.Equal(1, declined.ClearAsked);
        Assert.Equal(2, history.All().Count);                                // "no": nothing is deleted

        var accepted = new Dialogs(answer: true);
        await Settings(new Settings(), coordinator, accepted).ClearHistoryCommand.ExecuteAsync(null);
        Assert.Empty(history.All());
    }
}
