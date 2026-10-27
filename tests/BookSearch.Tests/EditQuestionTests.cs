using BookSearch.App;
using BookSearch.App.Ask;
using BookSearch.App.History;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using static BookSearch.Tests.SummaryKit;

namespace BookSearch.Tests;

/// <summary>Editing the newest question and searching again (ST-28).</summary>
public class EditQuestionTests
{
    private static (AskViewModel Vm, FakeAsk Ask, InMemoryHistory History, SummaryCoordinator Coordinator, FakeSummarizer Summarizer) Make(
        Settings? settings = null, Dialogs? dialogs = null, FakeRewriter? rewriter = null)
    {
        var ask = new FakeAsk();
        var history = new InMemoryHistory();
        var summarizer = new FakeSummarizer();
        var coordinator = Coordinator(summarizer, history, ask);
        var vm = new AskViewModel(ask, new Strings(), coordinator, settings ?? new Settings(), history, a => a(), dialogs ?? new Dialogs(), rewriter);
        return (vm, ask, history, coordinator, summarizer);
    }

    private static async Task Ask(AskViewModel vm, string question)
    {
        vm.Question = question;
        await vm.SearchCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Edit_PutsTheLastQuestionInTheBox_AndSearchingReplacesIt()
    {
        var (vm, ask, history, coordinator, _) = Make();
        using var _c = coordinator;
        await Ask(vm, "Who is Elizabeth Bennet in the novel");
        await Ask(vm, "Why did Darcy propose first time");
        var focused = 0;
        vm.FocusQuestionRequested += () => focused++;

        vm.Latest!.EditCommand.Execute(null);

        Assert.True(vm.IsEditing);
        Assert.Equal("Why did Darcy propose first time", vm.Question);
        Assert.Equal(1, focused);
        Assert.Equal(2, vm.Turns.Count);                                           // nothing is replaced until the search

        vm.Question = "Why did Darcy propose a second time";
        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();

        Assert.False(vm.IsEditing);
        Assert.Equal(["Who is Elizabeth Bennet in the novel", "Why did Darcy propose a second time"], vm.Turns.Select(t => t.Question));
        Assert.Equal(["Who is Elizabeth Bennet in the novel", "Why did Darcy propose a second time"], history.All().Select(e => e.Question));   // replaced, not added
        Assert.Equal(3, ask.Searches.Count);                                        // the edited question was searched
        Assert.Equal("Why did Darcy propose a second time", ask.Searches[^1].Question);
    }

    [Fact]
    public async Task Edit_OnlyTheNewestQuestionCanBeEdited()
    {
        var (vm, _, _, coordinator, _) = Make();
        using var _c = coordinator;
        await Ask(vm, "first question here");
        await Ask(vm, "second question here");

        Assert.False(vm.Turns[0].EditCommand.CanExecute(null));
        Assert.True(vm.Turns[1].EditCommand.CanExecute(null));
        vm.Turns[0].EditCommand.Execute(null);                                      // even if invoked, nothing happens
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public async Task Edit_SearchingTheSameQuestionAgain_RunsTheSearchOverAndReplacesTheAnswer()
    {
        var (vm, ask, history, coordinator, summarizer) = Make();
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        await coordinator.WhenIdleAsync();
        var firstEntry = vm.Latest!.EntryId;
        vm.Latest.EditCommand.Execute(null);

        await vm.SearchCommand.ExecuteAsync(null);                                   // unchanged: "search again"
        await coordinator.WhenIdleAsync();

        Assert.Single(vm.Turns);
        Assert.Equal(2, ask.Searches.Count);
        Assert.NotEqual(firstEntry, vm.Latest!.EntryId);                              // a new history entry...
        Assert.Single(history.All());                                                 // ...and the old one is gone
        Assert.Equal(["who is Darcy", "who is Darcy"], summarizer.Questions);          // a new summary is written
    }

    [Fact]
    public async Task Edit_UsesTheCurrentFilter_SoItCanBeChangedAndTheSearchRepeated()
    {
        var dialogs = new Dialogs { FilterAnswer = new SearchFilter(["novels"], []) };
        var (vm, ask, _, coordinator, _) = Make(dialogs: dialogs);
        ask.Options = new FilterOptions([new BookSearch.Core.Data.TagInfo("novels", 3)], []);
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        Assert.True(ask.Filters[0] is null or { IsEmpty: true });                    // asked over every book first

        await vm.ChooseFilterCommand.ExecuteAsync(null);                             // then limit it to the novels...
        vm.Latest!.EditCommand.Execute(null);
        await vm.SearchCommand.ExecuteAsync(null);                                   // ...and search the same question again

        Assert.Equal(["novels"], ask.Filters[^1]!.Tags);
        Assert.Single(vm.Turns);
    }

    [Fact]
    public async Task Edit_Cancel_LeavesEverythingAsItWas()
    {
        var (vm, ask, history, coordinator, _) = Make();
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        vm.Latest!.EditCommand.Execute(null);
        Assert.True(vm.CancelEditCommand.CanExecute(null));

        vm.CancelEditCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Equal("", vm.Question);
        Assert.Single(vm.Turns);
        Assert.Single(history.All());
        Assert.Single(ask.Searches);
        Assert.False(vm.CancelEditCommand.CanExecute(null));
    }

    [Fact]
    public async Task Edit_EmptiedBox_DoesNotDeleteTheQuestion()
    {
        var (vm, ask, history, coordinator, _) = Make();
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        vm.Latest!.EditCommand.Execute(null);
        vm.Question = "   ";

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Single(vm.Turns);
        Assert.Single(history.All());
        Assert.Single(ask.Searches);
    }

    [Fact]
    public async Task Edit_WhileItsSummaryIsBeingWritten_StopsThatSummary()
    {
        var (vm, _, history, coordinator, summarizer) = Make();
        using var _c = coordinator;
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        summarizer.OnRun = async (_, _, _, onToken, ct) =>
        {
            onToken("partial");
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return Run("x");
        };
        await Ask(vm, "who is Darcy");
        await started.Task;
        summarizer.OnRun = (_, _, _, _, _) => Task.FromResult(Run("A fact. [1]"));
        vm.Latest!.EditCommand.Execute(null);
        vm.Question = "who is Bingley";

        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));                      // the old summary was stopped
        Assert.Equal(["who is Bingley"], history.All().Select(e => e.Question));
        Assert.Equal(["who is Bingley"], vm.Turns.Select(t => t.Question));
    }

    [Fact]
    public async Task Edit_NewThread_EndsTheEdit()
    {
        var (vm, _, _, coordinator, _) = Make();
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        vm.Latest!.EditCommand.Execute(null);

        vm.NewThreadCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Empty(vm.Turns);
    }

    [Fact]
    public async Task Edit_AFollowUpIsRewrittenWithTheEarlierQuestions_NotWithTheOneBeingReplaced()
    {
        var rewriter = new FakeRewriter();
        var (vm, ask, _, coordinator, _) = Make(rewriter: rewriter);
        using var _c = coordinator;
        await Ask(vm, "Who is Elizabeth Bennet in the novel");
        await Ask(vm, "and her sister?");
        vm.Latest!.EditCommand.Execute(null);
        vm.Question = "and her father?";

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(["Who is Elizabeth Bennet in the novel"], rewriter.Calls[^1].Earlier);    // not "and her sister?"
        Assert.Equal("Who is Elizabeth Bennet in the novel -> and her father?", ask.Searches[^1].Question);
    }

    [Fact]
    public async Task Retry_AsksTheNewestQuestionAgain_ReplacingTheFirstAttempt()
    {
        var (vm, ask, history, coordinator, summarizer) = Make();
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        await coordinator.WhenIdleAsync();
        var firstEntry = vm.Latest!.EntryId;

        vm.Latest.RetryCommand.Execute(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 2 && vm.Latest is { IsSearching: false } l && l.EntryId != firstEntry && l.EntryId > 0);
        await coordinator.WhenIdleAsync();

        Assert.Single(vm.Turns);
        Assert.Equal(2, ask.Searches.Count);
        Assert.NotEqual(firstEntry, vm.Latest!.EntryId);
        Assert.Single(history.All());                                                 // the first attempt is gone from the History
        Assert.Equal(["who is Darcy", "who is Darcy"], summarizer.Questions);          // a new summary is written
    }

    [Fact]
    public async Task Retry_OnlyTheNewestQuestionHasIt()
    {
        var (vm, _, _, coordinator, _) = Make();
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        var first = vm.Turns[0];
        await Ask(vm, "who is Bingley");

        Assert.False(first.RetryCommand.CanExecute(null));
        Assert.True(vm.Latest!.RetryCommand.CanExecute(null));
        first.RetryCommand.Execute(null);                                             // even if it is run anyway, nothing happens
        Assert.Equal(["who is Darcy", "who is Bingley"], vm.Turns.Select(t => t.Question));
    }

    [Fact]
    public async Task Retry_LeavesWhatIsBeingTypedAlone()
    {
        var (vm, ask, _, coordinator, _) = Make();
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        vm.Question = "a half-written ques";

        vm.Latest!.RetryCommand.Execute(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 2 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.Equal("a half-written ques", vm.Question);
        Assert.Equal("who is Darcy", ask.Searches[^1].Question);
    }

    [Fact]
    public async Task Retry_AFollowUpThatCouldNotBeRewritten_IsRewrittenOnTheRetry()
    {
        var rewriter = new FakeRewriter { OnRewrite = (_, _) => throw new RewriteUnavailableException() };
        var (vm, ask, _, coordinator, _) = Make(rewriter: rewriter);
        using var _c = coordinator;
        await Ask(vm, "Who is Elizabeth Bennet in the novel");
        await Ask(vm, "and her sister?");
        Assert.True(vm.Latest!.HasWarning);                                           // searched as typed, with a warning
        rewriter.OnRewrite = (q, earlier) => $"{earlier[^1]} -> {q}";                  // the model is back

        vm.Latest.RetryCommand.Execute(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 3 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.Single(vm.Turns, t => t.Question == "and her sister?");
        Assert.False(vm.Latest!.HasWarning);
        Assert.Equal("Who is Elizabeth Bennet in the novel -> and her sister?", ask.Searches[^1].Question);
    }

    [Fact]
    public async Task Retry_AFollowUpThatWasRewritten_KeepsTheRewriteAndDoesNotAskTheModelAgain()
    {
        var rewriter = new FakeRewriter();
        var (vm, ask, history, coordinator, summarizer) = Make(rewriter: rewriter);
        using var _c = coordinator;
        await Ask(vm, "Who is Elizabeth Bennet in the novel");
        await Ask(vm, "and her sister?");
        Assert.Single(rewriter.Calls);

        vm.Latest!.RetryCommand.Execute(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 3 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.Single(rewriter.Calls);                                                // not asked again
        Assert.Equal("Who is Elizabeth Bennet in the novel -> and her sister?", ask.Searches[^1].Question);
        await coordinator.WhenIdleAsync();
        Assert.Equal("Who is Elizabeth Bennet in the novel -> and her sister?", history.All()[^1].SearchedAs);   // saved with the new entry
        Assert.Equal(2, history.All().Count);
        Assert.Equal("Who is Elizabeth Bennet in the novel -> and her sister?", summarizer.Questions[^1]);       // and the summary is written for it
        Assert.True(vm.Latest!.HasContext);                                           // the chip is still there
        Assert.Equal("Who is Elizabeth Bennet in the novel -> and her sister?", vm.Latest.ContextQuestion);
    }

    [Fact]
    public async Task Retry_ARestoredRewrittenFollowUp_KeepsItsSavedRewrite()
    {
        var history = new InMemoryHistory();
        var thread = history.AddThread();
        history.Add("and her sister?", SummaryStatus.Ready, null, thread, null, "Who is Elizabeth Bennet's sister?");
        var ask = new FakeAsk();
        var rewriter = new FakeRewriter();
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);
        var vm = new AskViewModel(ask, new Strings(), coordinator, new Settings(), history, a => a(), new Dialogs(), rewriter);
        await vm.ShowThreadAsync(thread);

        vm.Latest!.RetryCommand.Execute(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 1 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.Empty(rewriter.Calls);
        Assert.Equal("Who is Elizabeth Bennet's sister?", ask.Searches[0].Question);
        Assert.Equal("Who is Elizabeth Bennet's sister?", history.All().Single().SearchedAs);
    }

    [Theory]
    [InlineData(false)]    // automatic summaries turned off since
    [InlineData(true)]     // "earlier questions" set to 0 since
    public async Task Retry_WhenFollowUpHandlingIsNowOff_SearchesAsTypedInsteadOfKeepingTheRewrite(bool contextZero)
    {
        var rewriter = new FakeRewriter();
        var settings = new Settings();
        var (vm, ask, _, coordinator, _) = Make(settings, rewriter: rewriter);
        using var _c = coordinator;
        await Ask(vm, "Who is Elizabeth Bennet in the novel");
        await Ask(vm, "and her sister?");
        Assert.True(vm.Latest!.HasContext);
        settings.Current = contextZero ? settings.Current with { ContextQuestions = 0 } : settings.Current with { AutoSummary = false };

        vm.Latest.RetryCommand.Execute(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 3 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.Equal("and her sister?", ask.Searches[^1].Question);                    // as typed
        Assert.False(vm.Latest!.HasContext);
        Assert.Single(rewriter.Calls);
    }

    [Fact]
    public async Task Retry_AfterTheChipWasRemoved_StaysAsTyped()
    {
        var rewriter = new FakeRewriter();
        var (vm, ask, _, coordinator, _) = Make(rewriter: rewriter);
        using var _c = coordinator;
        await Ask(vm, "Who is Elizabeth Bennet in the novel");
        await Ask(vm, "and her sister?");
        vm.Latest!.RemoveContextCommand.Execute(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 3 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        vm.Latest!.RetryCommand.Execute(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 4 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.Equal("and her sister?", ask.Searches[^1].Question);
        Assert.Single(rewriter.Calls);                                                // the user's choice was not undone
    }

    [Fact]
    public async Task Retry_WhenNoBookIsReady_KeepsTheQuestionAndItsAnswer()
    {
        var (vm, ask, history, coordinator, _) = Make();
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        await coordinator.WhenIdleAsync();
        var entry = vm.Latest!.EntryId;
        ask.Library = new LibrarySummary(0, 2);                                       // every book is being indexed again

        vm.Latest.RetryCommand.Execute(null);
        await SummaryKit.Eventually(() => !vm.IsSearching);

        Assert.Equal(["who is Darcy"], vm.Turns.Select(t => t.Question));
        Assert.Equal(entry, vm.Latest!.EntryId);
        Assert.Equal(["who is Darcy"], history.All().Select(e => e.Question));        // nothing was deleted
    }

    [Fact]
    public async Task Retry_WhileItsSummaryIsBeingWritten_StopsThatSummary()
    {
        var (vm, _, history, coordinator, summarizer) = Make();
        using var _c = coordinator;
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        summarizer.OnRun = async (_, _, _, onToken, ct) =>
        {
            onToken("partial");
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return Run("x");
        };
        await Ask(vm, "who is Darcy");
        await started.Task;
        summarizer.OnRun = (_, _, _, _, _) => Task.FromResult(Run("A fact. [1]"));

        vm.Latest!.RetryCommand.Execute(null);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));                      // the old summary was stopped
        await SummaryKit.Eventually(() => vm.Latest is { IsSearching: false, EntryId: > 0 });
        await coordinator.WhenIdleAsync();

        Assert.Single(vm.Turns);
        Assert.Equal(["who is Darcy"], history.All().Select(e => e.Question));
    }

    [Fact]
    public async Task Retry_UsesTheCurrentFilter()
    {
        var dialogs = new Dialogs { FilterAnswer = new SearchFilter(["novels"], []) };
        var (vm, ask, _, coordinator, _) = Make(dialogs: dialogs);
        ask.Options = new FilterOptions([new BookSearch.Core.Data.TagInfo("novels", 3)], []);
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        await vm.ChooseFilterCommand.ExecuteAsync(null);

        vm.Latest!.RetryCommand.Execute(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 2 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.Equal(["novels"], ask.Filters[^1]!.Tags);
    }

    [Fact]
    public async Task Retry_PressedTwice_AsksOnce()
    {
        var (vm, ask, history, coordinator, _) = Make();
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        var turn = vm.Latest!;
        var gate = new TaskCompletionSource();
        ask.OnLibrary = () => gate.Task;                                              // both presses wait at the library check

        turn.RetryCommand.Execute(null);
        turn.RetryCommand.Execute(null);                                              // before the first has replaced anything
        gate.SetResult();
        await SummaryKit.Eventually(() => vm.Latest is { IsSearching: false, EntryId: > 0 } && !ReferenceEquals(vm.Latest, turn));
        await coordinator.WhenIdleAsync();

        Assert.Single(vm.Turns);
        Assert.Single(history.All());
        Assert.Equal(2, ask.Searches.Count);                                          // the first question and one retry, not two
    }

    [Fact]
    public async Task Retry_WhileTheFirstAttemptIsStillBeingRecorded_LeavesNoSecondEntry()
    {
        var history = new InMemoryHistory();
        history.Add("an unfinished question from last time", SummaryStatus.Waiting, null, history.AddThread());
        var ask = new FakeAsk();
        var gate = new TaskCompletionSource();
        ask.OnWarmUp = _ => gate.Task;                                                // the restart pass holds recording back
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);
        var vm = new AskViewModel(ask, new Strings(), coordinator, new Settings(), history, a => a(), new Dialogs());
        _ = coordinator.ResumeUnfinishedAsync();
        var asking = Ask(vm, "who is Darcy");
        await SummaryKit.Eventually(() => vm.Latest is { Results.Count: > 0, EntryId: 0 });

        vm.Latest!.RetryCommand.Execute(null);
        gate.SetResult();
        await asking;
        await SummaryKit.Eventually(() => vm.Latest is { IsSearching: false, EntryId: > 0 });
        await coordinator.WhenIdleAsync();

        Assert.Single(history.All(), e => e.Question == "who is Darcy");              // not one for each attempt
        Assert.Single(vm.Turns);
    }

    [Fact]
    public async Task Retry_WhileTheFirstAttemptIsBeingRecorded_AndTheLibraryCheckIsSlow_LeavesNoSecondEntry()
    {
        var history = new InMemoryHistory();
        history.Add("an unfinished question from last time", SummaryStatus.Waiting, null, history.AddThread());
        var ask = new FakeAsk();
        var warmUp = new TaskCompletionSource();
        ask.OnWarmUp = _ => warmUp.Task;                                              // recording waits for the restart pass
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);
        var vm = new AskViewModel(ask, new Strings(), coordinator, new Settings(), history, a => a(), new Dialogs());
        _ = coordinator.ResumeUnfinishedAsync();
        var asking = Ask(vm, "who is Darcy");
        await SummaryKit.Eventually(() => vm.Latest is { Results.Count: > 0, EntryId: 0 });
        var library = new TaskCompletionSource();
        ask.OnLibrary = () => library.Task;                                           // the retry's check of the library is slow

        vm.Latest!.RetryCommand.Execute(null);
        warmUp.SetResult();                                                           // the first attempt records its entry meanwhile
        await asking;
        library.SetResult();
        await SummaryKit.Eventually(() => vm.Latest is { IsSearching: false, EntryId: > 0 });
        await coordinator.WhenIdleAsync();

        Assert.Single(history.All(), e => e.Question == "who is Darcy");
        Assert.Single(vm.Turns);
    }

    [Fact]
    public async Task Retry_OfAQuestionStillBeingSearched_ThenANewQuestion_LeavesNoStuckSpinner()
    {
        var (vm, ask, _, coordinator, _) = Make();
        using var _c = coordinator;
        ask.OnSearch = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);                                   // the first search never finishes by itself
            return default!;
        };
        vm.Question = "who is Darcy";
        var first = vm.SearchCommand.ExecuteAsync(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 1);
        ask.OnSearch = null;
        var library = new TaskCompletionSource();
        ask.OnLibrary = () => library.Task;

        vm.Latest!.RetryCommand.Execute(null);                                        // retried while still searching...
        vm.Question = "who is Elizabeth";
        var second = vm.SearchCommand.ExecuteAsync(null);                             // ...and overtaken by a new question
        library.SetResult();
        await Task.WhenAll(first, second);
        await SummaryKit.Eventually(() => vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.Equal(["who is Elizabeth"], vm.Turns.Select(t => t.Question));
        Assert.DoesNotContain(vm.Turns, t => t.IsSearching);
    }

    [Fact]
    public async Task Retry_OfAQuestionStillBeingSearched_WithNoBookReady_DropsIt()
    {
        var (vm, ask, _, coordinator, _) = Make();
        using var _c = coordinator;
        ask.OnSearch = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return default!;
        };
        vm.Question = "who is Darcy";
        var first = vm.SearchCommand.ExecuteAsync(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 1);
        ask.Library = new LibrarySummary(0, 2);

        vm.Latest!.RetryCommand.Execute(null);
        await first;
        await SummaryKit.Eventually(() => !vm.IsSearching);

        Assert.Empty(vm.Turns);                                                       // documented: a question still being searched is dropped
    }

    [Fact]
    public async Task Retry_WhenClosingTheOldEntryFails_StillAsksTheQuestion()
    {
        var (vm, ask, history, coordinator, _) = Make();
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        await coordinator.WhenIdleAsync();
        history.DeleteFails = new IOException("the database is locked");

        vm.Latest!.RetryCommand.Execute(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 2 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.Single(vm.Turns);                                                      // the question was asked and shown
        Assert.Equal(2, history.All().Count);                                         // the old entry stayed: it could not be deleted
    }

    [Fact]
    public async Task Clear_WhileTheRestartPassHoldsItsList_StopsThePassFromWorkingThroughIt()
    {
        var history = new InMemoryHistory();
        var thread = history.AddThread();
        history.Add("first unfinished question", SummaryStatus.Waiting, null, thread);
        history.Add("second unfinished question", SummaryStatus.Waiting, null, thread);
        var ask = new FakeAsk();
        var summarizer = new FakeSummarizer();
        var listed = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        history.AfterListUnfinished = async () => { listed.TrySetResult(); await release.Task; };   // list read, not yet delivered
        using var coordinator = Coordinator(summarizer, history, ask);
        var pass = coordinator.ResumeUnfinishedAsync();
        await listed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.ClearAsync();                                               // the list in the pass's hand is now stale
        release.SetResult();
        await pass;
        await coordinator.WhenIdleAsync();

        Assert.Empty(summarizer.Questions);
        Assert.Empty(ask.Searches);                                                   // the deleted questions were not searched either
    }

    [Fact]
    public async Task Close_AnEntryThatTheRestartPassIsStillSearching_IsNotSummarized()
    {
        var history = new InMemoryHistory();
        var id = history.Add("an unfinished question from last time", SummaryStatus.Waiting, null, history.AddThread());
        var ask = new FakeAsk();
        var summarizer = new FakeSummarizer();
        var gate = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        ask.OnWarmUp = _ => { reached.TrySetResult(); return gate.Task; };           // the pass is searching for it
        using var coordinator = Coordinator(summarizer, history, ask);
        var pass = coordinator.ResumeUnfinishedAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.CloseEntryAsync(id);                                        // the user retried or closed it meanwhile
        gate.SetResult();
        await pass;
        await coordinator.WhenIdleAsync();

        Assert.Empty(summarizer.Questions);                                           // no summary written for nobody
        Assert.Empty(history.All());
    }

    [Fact]
    public async Task Close_AnEntryBeforeTheRestartPassReachesIt_IsNotSummarized()
    {
        var history = new InMemoryHistory();
        var thread = history.AddThread();
        history.Add("first unfinished question", SummaryStatus.Waiting, null, thread);
        var second = history.Add("second unfinished question", SummaryStatus.Waiting, null, thread);
        var ask = new FakeAsk();
        var summarizer = new FakeSummarizer();
        var gate = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        ask.OnWarmUp = _ => { reached.TrySetResult(); return gate.Task; };           // held while the first one is searched
        using var coordinator = Coordinator(summarizer, history, ask);
        var pass = coordinator.ResumeUnfinishedAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.CloseEntryAsync(second);                                    // before the pass has picked it up
        gate.SetResult();
        await pass;
        await coordinator.WhenIdleAsync();

        Assert.Equal(["first unfinished question"], summarizer.Questions);
    }

    [Fact]
    public async Task Clear_WhileTheRestartPassIsWorking_StopsThePass()
    {
        var history = new InMemoryHistory();
        var thread = history.AddThread();
        history.Add("first unfinished question", SummaryStatus.Waiting, null, thread);
        history.Add("second unfinished question", SummaryStatus.Waiting, null, thread);
        var ask = new FakeAsk();
        var summarizer = new FakeSummarizer();
        var gate = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        ask.OnWarmUp = _ => { reached.TrySetResult(); return gate.Task; };
        using var coordinator = Coordinator(summarizer, history, ask);
        var pass = coordinator.ResumeUnfinishedAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.ClearAsync();
        gate.SetResult();
        await pass;
        await coordinator.WhenIdleAsync();

        Assert.Empty(summarizer.Questions);                                           // nothing is written for a cleared history
    }

    [Fact]
    public async Task Retry_WhileEditingThatQuestion_DropsTheEdit()
    {
        var (vm, ask, _, coordinator, _) = Make();
        using var _c = coordinator;
        await Ask(vm, "who is Darcy");
        vm.Latest!.EditCommand.Execute(null);
        vm.Question = "who is Bingley";

        vm.Latest.RetryCommand.Execute(null);
        await SummaryKit.Eventually(() => ask.Searches.Count == 2 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.False(vm.IsEditing);
        Assert.Equal("", vm.Question);
        Assert.Equal("who is Darcy", ask.Searches[^1].Question);                        // the original question, not the edited text
        Assert.Single(vm.Turns);
    }

    [Fact]
    public async Task Edit_ARestoredNewestQuestion_CanBeEditedToo()
    {
        var history = new InMemoryHistory();
        var thread = history.AddThread();
        history.Add("an old question from last time", SummaryStatus.Ready, null, thread);
        var ask = new FakeAsk();
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);
        var vm = new AskViewModel(ask, new Strings(), coordinator, new Settings(), history, a => a(), new Dialogs());
        await vm.ShowThreadAsync(thread);

        vm.Latest!.EditCommand.Execute(null);
        vm.Question = "a changed question";
        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();

        Assert.Equal(["a changed question"], vm.Turns.Select(t => t.Question));
        Assert.Equal(["a changed question"], history.All().Select(e => e.Question));
    }
}
