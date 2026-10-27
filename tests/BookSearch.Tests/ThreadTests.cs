using BookSearch.App.Ask;
using BookSearch.Core;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using Microsoft.Data.Sqlite;

namespace BookSearch.Tests;

/// <summary>Conversation threads (ST-24): storage, follow-up detection and the summary prompt.</summary>
public class ThreadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "threads-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ---- storage ----------------------------------------------------------------------------------------------

    [Fact]
    public void Migration4_WipesExistingHistory()
    {
        using var conn = Database.Open(Path.Combine(_dir, "library.db"));
        Migrator.Migrate(conn);
        // Rebuild what version 3 looked like: a history without threads, with rows in it.
        new SqliteCommand("DROP TABLE reading_position; DROP TABLE book_tag; DROP TABLE search_history; DROP TABLE search_thread; UPDATE app_meta SET value = '3' WHERE key = 'schema_version'", conn).ExecuteNonQuery();
        new SqliteCommand("""
            CREATE TABLE search_history (id INTEGER PRIMARY KEY, question TEXT NOT NULL, created_at TEXT NOT NULL,
              summary_status TEXT NOT NULL, summary_text TEXT, summary_model TEXT, summary_ms INTEGER);
            INSERT INTO search_history(question, created_at, summary_status) VALUES ('old', '2026-01-01T00:00:00Z', 'ready');
            """, conn).ExecuteNonQuery();

        var result = Migrator.Migrate(conn);

        Assert.Equal(3, result.FromVersion);
        Assert.Equal(Migrator.LatestVersion, result.ToVersion);
        var repo = new SearchHistoryRepository(conn);
        Assert.Empty(repo.List());                                           // the old entries are gone
        var thread = repo.AddThread();
        repo.Add("new", SummaryStatus.None, threadId: thread);
        Assert.Single(repo.ListThread(thread));
    }

    [Fact]
    public void Repository_Threads_ListWithTitleCountAndLastActivity_AndDeleteCascades()
    {
        using var conn = Database.Open(Path.Combine(_dir, "library.db"));
        Migrator.Migrate(conn);
        var repo = new SearchHistoryRepository(conn);
        var t0 = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        var first = repo.AddThread(t0);
        repo.Add("first question", SummaryStatus.Ready, t0, first);
        repo.Add("second question", SummaryStatus.Ready, t0.AddHours(1), first);
        var other = repo.AddThread(t0.AddDays(1));
        repo.Add("other", SummaryStatus.None, t0.AddDays(1), other);

        var threads = repo.ListThreads();

        Assert.Equal([other, first], threads.Select(t => t.Id));               // latest question first
        Assert.Equal("first question", threads[1].Title);
        Assert.Equal(2, threads[1].Questions);
        Assert.Equal(t0.AddHours(1), threads[1].UpdatedAt);
        Assert.Equal(["first question", "second question"], repo.ListThread(first).Select(e => e.Question));

        repo.DeleteThread(first);
        Assert.Equal(["other"], repo.List().Select(e => e.Question));
        Assert.False(repo.ThreadExists(first));
    }

    [Fact]
    public void Repository_DeletingTheOnlyQuestion_KeepsTheThreadButHidesIt()
    {
        using var conn = Database.Open(Path.Combine(_dir, "library.db"));
        Migrator.Migrate(conn);
        var repo = new SearchHistoryRepository(conn);
        var thread = repo.AddThread();
        var id = repo.Add("q", SummaryStatus.None, threadId: thread);

        repo.Delete(id);

        Assert.Empty(repo.ListThreads());
        Assert.True(repo.ThreadExists(thread));                                 // so the question can be asked again inside it
    }

    // ---- follow-ups -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("and his sister?")]
    [InlineData("what about Elizabeth in the later chapters of the book")]
    [InlineData("She wrote that in which of the chapters near the end of the book")]
    [InlineData("y su hermana en el libro que leimos antes")]
    [InlineData("und seine Schwester im ersten Kapitel des Buches")]
    [InlineData("Why does it break down?")]
    [InlineData("What made them leave the town in the middle of the night")]
    [InlineData("Pourquoi est-ce que cela arrive si souvent dans le livre")]
    public void FollowUp_StartsWithOrContainsAPronoun_GivesTheRewriterThePreviousQuestion(string question)
    {
        var earlier = FollowUp.Detect(question, ["Who is Darcy in Pride and Prejudice?"]);

        Assert.Equal(["Who is Darcy in Pride and Prejudice?"], earlier);
    }

    [Theory]
    [InlineData("What happens to Captain Ahab at the end of the voyage")]
    [InlineData("Explain the main argument of the first chapter in detail")]
    [InlineData("这本书的第一章主要讲了什么内容呢请详细说明")]
    [InlineData("Who is Darcy?")]                      // short, but nothing in it points back: length is not a gate
    [InlineData("Why do people crave connection?")]
    [InlineData("Don Quijote ataca los molinos el primer dia de su viaje")]   // "el" is an article: only checked at the start
    public void FollowUp_WithoutAPronoun_IsNotRewritten(string question) =>
        Assert.Null(FollowUp.Detect(question, ["Who is Darcy?"]));

    [Fact]
    public void FollowUp_WithoutAnEarlierQuestion_HasNoContext() =>
        Assert.Null(FollowUp.Detect("and his sister?", []));

    [Fact]
    public void FollowUp_Default_UsesOnlyTheLastQuestion() =>
        Assert.Equal(["q4"], FollowUp.Detect("and his sister?", ["q1", "q2", "q3", "q4"]));

    [Theory]
    [InlineData(2, "q3,q4")]
    [InlineData(3, "q2,q3,q4")]
    public void FollowUp_UsesTheLastNQuestions_OldestFirst(int count, string expected) =>
        Assert.Equal(expected.Split(','), FollowUp.Detect("and his sister?", ["q1", "q2", "q3", "q4"], count));

    [Fact]
    public void FollowUp_MoreRequestedThanAsked_UsesWhatThereIs() =>
        Assert.Equal(["q1", "q2"], FollowUp.Detect("and his sister?", ["q1", "q2"], 3));

    [Fact]
    public void FollowUp_ZeroQuestions_TurnsFollowUpHandlingOff() =>
        Assert.Null(FollowUp.Detect("and his sister?", ["q1", "q2"], 0));

    // ---- the rewriter -----------------------------------------------------------------------------------------

    [Fact]
    public void Rewriter_Prompt_HasOnlyTheEarlierQuestionsAndTheLastOne()
    {
        var messages = QuestionRewriter.Build("What's its shadow strength?", ["What does despair do to a person?"]);

        Assert.Equal("system", messages[0].Role);
        Assert.Contains("Earlier questions:\n- What does despair do to a person?\n", messages[1].Content);
        Assert.EndsWith("Last question: What's its shadow strength?", messages[1].Content);
    }

    [Theory]
    [InlineData("What is despair's shadow strength?", "What is despair's shadow strength?")]
    [InlineData("  \"What is despair's shadow strength?\"  ", "What is despair's shadow strength?")]
    [InlineData("Rewritten question: What is despair's shadow strength?", "What is despair's shadow strength?")]
    [InlineData("**What is despair's shadow strength?**\nBecause it refers to despair.", "What is despair's shadow strength?")]
    [InlineData("\n\nWhat is despair's shadow strength?\n", "What is despair's shadow strength?")]
    public void Rewriter_Clean_KeepsTheQuestionLineOnly(string reply, string expected) =>
        Assert.Equal(expected, QuestionRewriter.Clean(reply));

    [Fact]
    public void Rewriter_Clean_RejectsEmptyAndRunawayReplies()
    {
        Assert.Null(QuestionRewriter.Clean("   \n  "));
        Assert.Null(QuestionRewriter.Clean(new string('x', QuestionRewriter.MaxLength + 1)));
    }

    [Fact]
    public async Task Rewriter_RewriteAsync_ReturnsTheStandaloneQuestion_WithAColdDeterministicRequest()
    {
        var chat = new FakeChatModel(["What is ", "despair's shadow strength?"]);

        var rewritten = await new QuestionRewriter(chat).RewriteAsync("What's its shadow strength?", ["What does despair do to a person?"]);

        Assert.Equal("What is despair's shadow strength?", rewritten);
        Assert.Equal(0, chat.Options!.Temperature);
        Assert.True(chat.Options.MaxTokens <= 100);
    }

    [Fact]
    public async Task Rewriter_RewriteAsync_AnUnchangedQuestionOrAnEmptyReply_IsNull()
    {
        Assert.Null(await new QuestionRewriter(new FakeChatModel(["why", "?"])).RewriteAsync("Why?", ["q"]));
        Assert.Null(await new QuestionRewriter(new FakeChatModel([""])).RewriteAsync("Why?", ["q"]));
    }

    [Fact]
    public async Task FollowUpRewriter_AModelFailure_BecomesRewriteUnavailable()
    {
        var failing = new FollowUpRewriter(new ThrowingChat(new ModelUnavailableException("no model")));
        await Assert.ThrowsAsync<RewriteUnavailableException>(() => failing.RewriteAsync("why?", ["q"], CancellationToken.None));

        // a cancellation the caller did not ask for (a dropped request) is a model failure too
        var dropped = new FollowUpRewriter(new ThrowingChat(new OperationCanceledException()));
        await Assert.ThrowsAsync<RewriteUnavailableException>(() => dropped.RewriteAsync("why?", ["q"], CancellationToken.None));
    }

    [Fact]
    public async Task FollowUpRewriter_AFailure_IsLoggedWithItsReasonOnly()
    {
        var lines = new List<string>();
        var failing = new FollowUpRewriter(new ThrowingChat(new ModelUnavailableException("secret question text")), log: lines.Add);
        await Assert.ThrowsAsync<RewriteUnavailableException>(() => failing.RewriteAsync("q", ["p"], default));
        var slow = new FollowUpRewriter(new HangingChat(), TimeSpan.FromMilliseconds(50), lines.Add, chatReady: () => false);
        await Assert.ThrowsAsync<RewriteUnavailableException>(() => slow.RewriteAsync("q", ["p"], default));

        Assert.Contains("ModelUnavailableException", lines[0]);
        Assert.Contains("time limit reached", lines[1]);
        Assert.Contains("summary queue or restart work busy at the start: no", lines[1]);
        Assert.Contains("chat server ready at the start: no", lines[1]);
        Assert.All(lines, l => Assert.DoesNotContain("secret", l));
    }

    [Fact]
    public async Task FollowUpRewriter_ALogThatThrows_StillFallsBackToTheTypedQuestion()
    {
        var failing = new FollowUpRewriter(new ThrowingChat(new ModelUnavailableException("no")),
            log: _ => throw new UnauthorizedAccessException());

        await Assert.ThrowsAsync<RewriteUnavailableException>(() => failing.RewriteAsync("q", ["p"], default));
    }

    [Fact]
    public async Task FollowUpRewriter_WhileASummaryIsWritten_TheTimeLimitWaitsForItToFinish()
    {
        var summary = new TaskCompletionSource();
        var rewriter = new FollowUpRewriter(new HangingChat(), TimeSpan.FromMilliseconds(100), afterSummary: () => summary.Task);

        var rewrite = rewriter.RewriteAsync("why?", ["q"], CancellationToken.None);
        await Task.Delay(400);                                  // four times the limit, but the summary is still being written
        Assert.False(rewrite.IsCompleted);

        summary.SetResult();                                    // now the limit starts
        await Assert.ThrowsAsync<RewriteUnavailableException>(() => rewrite);
    }

    [Fact]
    public async Task FollowUpRewriter_WhileASummaryIsWritten_ARewriteThatFinishesFirstIsUsed()
    {
        var summary = new TaskCompletionSource();               // never completes during the test
        var rewriter = new FollowUpRewriter(new FakeChatModel(["Why do we lose connection?"]), afterSummary: () => summary.Task);

        Assert.Equal("Why do we lose connection?", await rewriter.RewriteAsync("Why do we lose it?", ["Why do we need connection?"], default));
    }

    [Fact]
    public async Task FollowUpRewriter_CancelledWhileASummaryIsWritten_IsACancellation()
    {
        var summary = new TaskCompletionSource();
        var rewriter = new FollowUpRewriter(new HangingChat(), afterSummary: () => summary.Task);
        using var cts = new CancellationTokenSource();

        var rewrite = rewriter.RewriteAsync("why?", ["q"], cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rewrite);
    }

    [Fact]
    public async Task FollowUpRewriter_ASlowModel_IsGivenUpAfterTheTimeout()
    {
        var slow = new FollowUpRewriter(new HangingChat(), TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<RewriteUnavailableException>(() => slow.RewriteAsync("why?", ["q"], CancellationToken.None));
    }

    [Fact]
    public async Task FollowUpRewriter_CancelledByTheCaller_StaysCancellation()
    {
        var rewriter = new FollowUpRewriter(new HangingChat(), TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        var pending = rewriter.RewriteAsync("why?", ["q"], cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class HangingChat : IChatModel
    {
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    private sealed class ThrowingChat(Exception error) : IChatModel
    {
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw error;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public void History_SearchedAs_IsSavedWithTheEntry()
    {
        using var db = new TestDb();
        var repo = new SearchHistoryRepository(db.Conn);

        var id = repo.Add("What's its shadow strength?", SummaryStatus.None, searchedAs: "What is despair's shadow strength?");
        var plain = repo.Add("a question", SummaryStatus.None);

        Assert.Equal("What is despair's shadow strength?", repo.Get(id)!.SearchedAs);
        Assert.Null(repo.Get(plain)!.SearchedAs);
    }

    // ---- the prompt -------------------------------------------------------------------------------------------

    [Fact]
    public void Prompt_HasTheQuestionAndThePassage_AndNoEarlierQuestions()
    {
        var user = SummaryPrompt.Build("What is despair's shadow strength?", "Book", "Text.")[1].Content;

        Assert.StartsWith("Question: What is despair's shadow strength?", user);
        Assert.DoesNotContain("Earlier questions", user);
    }

    [Fact]
    public void Search_Defaults_AreThreeResults()
    {
        var options = new SearchOptions();

        Assert.Equal(3, options.TopN);
    }
}
