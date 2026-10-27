using BookSearch.App;
using BookSearch.App.Ask;
using BookSearch.App.History;
using BookSearch.Core;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using Microsoft.Data.Sqlite;
using static BookSearch.Tests.SummaryKit;

namespace BookSearch.Tests;

/// <summary>DCR-11 / ST-38: a question is also searched in the other languages of the library.</summary>
public class TranslatedSearchTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private const string English = "Why does the knight charge the windmills?";
    private const string Chinese = "骑士为什么冲向风车？";

    // ---- the search itself ---------------------------------------------------------------------------------------

    private static float[] Axis(int axis, double similarity, int other)
    {
        var v = new float[Database.EmbeddingDimension];
        v[axis] = (float)similarity;
        v[other] = (float)Math.Sqrt(1 - similarity * similarity);
        return v;
    }

    private long Book(string title, string language)
    {
        var id = _db.AddBook(title);
        new SqliteCommand($"UPDATE book SET language = '{language}' WHERE id = {id}", _db.Conn).ExecuteNonQuery();
        return id;
    }

    /// <summary>Three English passages that share the English question's words, and one Chinese passage about the same scene.</summary>
    private (long English, long Chinese) Library(double chineseMeaning = 0.60)
    {
        var en = Book("Don Quixote", "en");
        var zh = Book("堂吉诃德", "zh");
        for (var i = 0; i < 3; i++)
            _db.Add(_db.Write(en, i, $"The knight charges the windmills believing them giants {i}", Axis(0, 0.62, 10 + i)));
        _db.Add(_db.Write(zh, 0, "骑士冲向风车，以为那是巨人", Axis(1, chineseMeaning, 20), cjk: true));
        return (en, zh);
    }

    // The English question points along axis 0 and its Chinese translation along axis 1.
    private SearchService Service(FakeQueryEmbedder? embedder = null) =>
        new(_db.Conn, embedder ?? new FakeQueryEmbedder(q => TestDb.Vec(q == English ? 1 : 0, q == English ? 0 : 1)));

    [Fact]
    public async Task Search_WithoutTheTranslation_TheOtherLanguageLosesToTheQuestionsOwn()
    {
        var (_, zh) = Library();

        var result = await Service().SearchAsync(English);

        Assert.DoesNotContain(result.Hits, h => h.BookId == zh);       // the keyword lists can only hold English passages
    }

    [Fact]
    public async Task Search_WithTheTranslation_TheOtherLanguageIsFound_AndRanksFirstHere()
    {
        var (en, zh) = Library();

        var result = await Service().SearchAsync(English, alsoSearch: [Chinese]);

        Assert.Equal(zh, result.Hits[0].BookId);                       // found by both its own meaning list and the trigram list
        Assert.Contains(result.Hits, h => h.BookId == en);             // the English passages are still there
        Assert.Equal(3, result.Hits.Count);
        Assert.NotNull(result.Hits[0].VectorRank);
        Assert.NotNull(result.Hits[0].TrigramRank);
    }

    [Fact]
    public async Task Search_WithTheTranslation_HighlightsTheTermsOfBothQueries()
    {
        Library();

        var result = await Service().SearchAsync(English, alsoSearch: [Chinese]);

        Assert.Contains("windmills", result.Terms.Words);
        Assert.NotEmpty(result.Terms.Trigrams);
        Assert.NotEmpty(result.Hits[0].Highlights);                    // the Chinese passage is marked by the Chinese query
    }

    [Fact]
    public async Task Search_Coverage_ATranslatedQueryThatClearsTheNormalThreshold_CoversTheQuestion()
    {
        var zh = Book("堂吉诃德", "zh");
        _db.Add(_db.Write(zh, 0, "完全不同的一段话，没有任何共同的字", Axis(1, 0.50, 20)));     // 0.50 to the translation, 0 to the English question

        var alone = await Service().SearchAsync(English);
        var both = await Service().SearchAsync(English, alsoSearch: [Chinese]);

        Assert.True(alone.NotCovered);
        Assert.False(both.NotCovered);                                 // 0.50 clears 0.45, the threshold for a same-language passage
    }

    [Fact]
    public async Task Search_Coverage_WithTranslations_EveryQueryUsesTheNormalThreshold_AndNeedsNoLanguageOfTheQuestion()
    {
        var zh = Book("堂吉诃德", "zh");
        // 0.40 to the English question and also 0.40 to its Chinese translation: below 0.45, above the cross-language 0.35.
        var v = new float[Database.EmbeddingDimension];
        v[0] = 0.40f; v[1] = 0.40f; v[20] = MathF.Sqrt(1 - 0.32f);
        _db.Add(_db.Write(zh, 0, "完全不同的一段话，没有任何共同的字", v));

        var alone = await Service().SearchAsync(English);                                       // English question, Chinese passage: cross-language 0.35
        var both = await Service().SearchAsync(English, alsoSearch: [Chinese]);                  // the Chinese query is held to 0.45
        var unknown = await Service().SearchAsync("Why does Darcy love Elizabeth?");             // "und": the normal threshold, as before

        var blank = await Service().SearchAsync(English, alsoSearch: [" ", English]);            // no real translation: the old rule stays

        Assert.False(alone.NotCovered);
        Assert.True(both.NotCovered);
        Assert.True(unknown.NotCovered);
        Assert.False(blank.NotCovered);
    }

    [Fact]
    public async Task Search_AlsoSearch_BlankOrRepeatedQueries_AreSearchedOnce()
    {
        Library();
        var embedder = new FakeQueryEmbedder(_ => TestDb.Vec(1));

        await Service(embedder).SearchAsync(English, alsoSearch: [" ", English.ToUpperInvariant(), English]);

        Assert.Equal(1, embedder.Calls);
    }

    [Fact]
    public async Task Search_AlsoSearch_WhenTheEmbeddingServerFails_ReturnsTheKeywordMatchesOfEveryQuery()
    {
        Library();
        var embedder = new FakeQueryEmbedder(_ => throw new ModelUnavailableException("down"));

        var result = await Service(embedder).SearchAsync(English, alsoSearch: [Chinese]);

        Assert.True(result.KeywordOnly);
        Assert.Contains(result.Hits, h => h.Text.Contains("骑士"));      // the Chinese passage is found by its trigrams alone
        Assert.Contains(result.Hits, h => h.Text.Contains("windmills"));
    }

    [Fact]
    public async Task Search_NoTranslation_IsAsBefore()
    {
        Library();

        var a = await Service().SearchAsync(English);
        var b = await Service().SearchAsync(English, alsoSearch: []);

        Assert.Equal(a.Hits.Select(h => h.ChunkId), b.Hits.Select(h => h.ChunkId));
        Assert.Equal(a.Hits.Select(h => h.Score), b.Hits.Select(h => h.Score));
    }

    // ---- which languages ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Why does the knight charge the windmills?")]
    [InlineData("骑士为什么冲向风车？")]
    [InlineData("Why does Darcy love Elizabeth?")]     // none of these needs the question's language: it is not guessed
    [InlineData("stoic virtue")]
    [InlineData("1984")]
    public void Targets_AMultiLanguageLibrary_IsAskedForInEveryLanguage_MostBooksFirst_AtMostThree(string question)
    {
        Assert.Equal(["zh", "en"], TranslationTargets.Choose(question, ["zh", "en"]));
        Assert.Equal(["zh", "en", "ja"], TranslationTargets.Choose(question, ["zh", "en", "ja", "ar"]));
    }

    [Fact]
    public void Targets_ALibraryInOneLanguage_TranslatesOnlyAQuestionClearlyInAnotherLanguage()
    {
        Assert.Equal(["en"], TranslationTargets.Choose(Chinese, ["en", "en"]));            // the one place the detector is still used
        Assert.Empty(TranslationTargets.Choose(English, ["en"]));                          // the same language: no model call
        Assert.Empty(TranslationTargets.Choose("Why does Darcy love Elizabeth?", ["en"])); // unknown: as before, no translation
    }

    [Fact]
    public void Targets_NoneWhenNothingCanBeGained()
    {
        Assert.Empty(TranslationTargets.Choose(English, []));                              // no books
        Assert.Empty(TranslationTargets.Choose(Chinese, [null, "und", "xx"]));             // unknown or unnamed languages are not asked for
        Assert.Empty(TranslationTargets.Choose("stoic virtue", ["und", "en"]));            // "und" is not a language of the library
    }

    [Fact]
    public void Targets_AreListedOnceAndInLowerCase()
    {
        Assert.Equal(["zh", "en"], TranslationTargets.Choose(English, ["ZH", "zh", "en"]));
    }

    // ---- the translator ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Translator_AsksForEveryLanguageByName_AndReturnsThemInOrder()
    {
        var chat = new ScriptedChatModel("Chinese: 骑士为什么冲向风车？\nJapanese: なぜ騎士は風車に突撃するのか？");

        var result = await new QueryTranslator(chat).TranslateAsync(English, ["zh", "ja"]);

        Assert.Equal(["骑士为什么冲向风车？", "なぜ騎士は風車に突撃するのか？"], result);
        Assert.Contains("Languages: Chinese, Japanese", chat.Prompts[0][1].Content);
        Assert.Contains(English, chat.Prompts[0][1].Content);
        Assert.Equal(0, chat.Options!.Temperature);
    }

    [Fact]
    public async Task Translator_NoLanguages_NoModelCall()
    {
        var chat = new ScriptedChatModel("x");

        Assert.Empty(await new QueryTranslator(chat).TranslateAsync(English, []));
        Assert.Empty(chat.Prompts);
    }

    [Theory]
    [InlineData("Chinese: 骑士为什么冲向风车？")]
    [InlineData("**Chinese:** \"骑士为什么冲向风车？\"")]
    [InlineData("- Chinese：骑士为什么冲向风车？")]
    [InlineData("骑士为什么冲向风车？")]                       // a lone answer without its label
    public void Translator_Parse_AcceptsTheCommonShapesOfAReply(string reply)
    {
        Assert.Equal([Chinese], QueryTranslator.Parse(reply, English, ["zh"]));
    }

    [Fact]
    public void Translator_Parse_SkipsALanguageThatIsMissingOrJustRepeatsTheQuestion()
    {
        Assert.Equal(["骑士为什么冲向风车？"], QueryTranslator.Parse("Chinese: 骑士为什么冲向风车？\nJapanese: " + English, English, ["zh", "ja"]));
        Assert.Equal(["骑士为什么冲向风车？"], QueryTranslator.Parse("Chinese: 骑士为什么冲向风车？", English, ["zh", "ja"]));   // two languages: no guessing for a missing label
        Assert.Empty(QueryTranslator.Parse("", English, ["zh"]));
        Assert.Empty(QueryTranslator.Parse("Chinese:", English, ["zh"]));
        Assert.Empty(QueryTranslator.Parse("Chinese: " + new string('字', QueryTranslator.MaxLength + 1), English, ["zh"]));
    }

    [Fact]
    public void Translator_Parse_ALoneReplyWithAPreambleIsNotTakenAsTheTranslation()
    {
        Assert.Empty(QueryTranslator.Parse("Sure, here it is:\n" + Chinese, English, ["zh"]));
    }

    // ---- over the chat server -------------------------------------------------------------------------------------------

    private sealed class ThrowingChat(Exception error) : IChatModel
    {
        public IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw error;
    }

    [Fact]
    public async Task QuestionTranslator_AModelFailure_BecomesTranslationUnavailable_AndIsLoggedWithoutTheQuestion()
    {
        var lines = new List<string>();
        var failing = new QuestionTranslator(new ThrowingChat(new ModelUnavailableException("secret question text")), log: lines.Add);

        await Assert.ThrowsAsync<TranslationUnavailableException>(() => failing.TranslateAsync("secret question text", ["zh"], default));

        var line = Assert.Single(lines);
        Assert.Contains("translation of the question failed", line);
        Assert.DoesNotContain("secret", line);
    }

    [Fact]
    public async Task QuestionTranslator_TheCallerCancelling_IsNotAFailure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var chat = new QuestionTranslator(new ScriptedChatModel("Chinese: x"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.TranslateAsync(English, ["zh"], cts.Token));
    }

    [Fact]
    public void ChatSideCall_TheLimitIsLongerWhenTheCallHasToStartTheChatServer()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), ChatSideCall.LimitFor(true));
        Assert.Equal(TimeSpan.FromSeconds(15), ChatSideCall.LimitFor(null));                                   // unknown: treated as running
        Assert.Equal(TimeSpan.FromSeconds(60), ChatSideCall.LimitFor(false));
        Assert.Equal(TimeSpan.FromMilliseconds(50), ChatSideCall.LimitFor(false, TimeSpan.FromMilliseconds(50)));   // an explicit limit wins
    }

    // ---- the history ---------------------------------------------------------------------------------------------------

    [Fact]
    public void History_AlsoSearched_IsSavedWithTheEntry()
    {
        var repo = new SearchHistoryRepository(_db.Conn);

        var id = repo.Add(English, SummaryStatus.None, alsoSearched: [Chinese, "なぜ"]);
        var plain = repo.Add("a question", SummaryStatus.None);
        var empty = repo.Add("another", SummaryStatus.None, alsoSearched: []);

        Assert.Equal([Chinese, "なぜ"], repo.Get(id)!.AlsoSearched);
        Assert.Null(repo.Get(plain)!.AlsoSearched);
        Assert.Null(repo.Get(empty)!.AlsoSearched);
    }

    [Fact]
    public async Task Restart_AnUnfinishedQuestion_IsSearchedWithItsSavedTranslations_WithoutAskingTheModel()
    {
        var history = new InMemoryHistory();
        var thread = history.AddThread();
        history.Add(English, SummaryStatus.Waiting, threadId: thread, alsoSearched: [Chinese]);
        var ask = new FakeAsk();
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);

        await coordinator.ResumeUnfinishedAsync();
        await coordinator.WhenIdleAsync();

        Assert.Equal([English], ask.Searches.Select(x => x.Question));
        Assert.Equal([Chinese], ask.AlsoSearches.Single());
    }

    // ---- the Ask page ---------------------------------------------------------------------------------------------------

    private static AskViewModel Page(FakeAsk ask, SummaryCoordinator coordinator, FakeTranslator? translator, AppSettings? settings = null,
        InMemoryHistory? history = null, string question = English) =>
        new(ask, new Strings(), coordinator, new Settings { Current = settings ?? new AppSettings() }, history ?? new InMemoryHistory(),
            translator: translator) { Question = question };

    [Fact]
    public async Task Ask_ABookInAnotherLanguage_TheQuestionIsTranslated_SearchedInBoth_AndTheChipSaysSo()
    {
        var ask = new FakeAsk { BookLanguages = ["en", "zh"] };
        var translator = new FakeTranslator { OnTranslate = (_, _) => [Chinese] };
        var history = new InMemoryHistory();
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);
        var vm = Page(ask, coordinator, translator, history: history);

        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();

        var call = Assert.Single(translator.Calls);
        Assert.Equal(English, call.Question);
        Assert.Equal(["en", "zh"], call.Languages);          // every language of the library: the question's own comes back unchanged and is dropped
        Assert.Equal([Chinese], ask.AlsoSearches.Single());
        Assert.True(vm.Latest!.HasAlsoSearched);
        Assert.True(vm.Latest.HasChips);
        Assert.False(vm.Latest.HasChipRow);                            // the first chip row is the filter and the rewrite; this chip has its own
        Assert.Equal("Ask_AlsoSearched", vm.Latest.AlsoSearchedText);
        Assert.Equal([Chinese], history.All().Single().AlsoSearched);
        Assert.Null(vm.Latest.Warning);
    }

    [Fact]
    public async Task Ask_AFollowUp_IsTranslatedFromItsRewrittenForm()
    {
        var ask = new FakeAsk { BookLanguages = ["en", "zh"] };
        var rewriter = new FakeRewriter { OnRewrite = (_, _) => "Who is Elizabeth Bennet's sister?" };
        var translator = new FakeTranslator();
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = new AskViewModel(ask, new Strings(), coordinator, new Settings(), new InMemoryHistory(), rewriter: rewriter, translator: translator)
        { Question = "Who is Elizabeth Bennet in the novel" };
        await vm.SearchCommand.ExecuteAsync(null);

        vm.Question = "and her sister?";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("Who is Elizabeth Bennet's sister?", translator.Calls[^1].Question);   // what was searched, not what was typed
    }

    [Fact]
    public async Task Ask_AllBooksInTheQuestionsLanguage_NoModelCall_AndNoChip()
    {
        var ask = new FakeAsk { BookLanguages = ["en", "en"] };
        var translator = new FakeTranslator();
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = Page(ask, coordinator, translator);

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Empty(translator.Calls);
        Assert.Empty(ask.AlsoSearches.Single() ?? []);
        Assert.False(vm.Latest!.HasAlsoSearched);
    }

    [Theory]
    [InlineData(false, true)]      // the setting is off
    [InlineData(true, false)]      // automatic summaries are off: the chat model is not kept loaded, so it is not woken for this
    public async Task Ask_TranslationOff_SearchesTheQuestionAlone(bool searchOtherLanguages, bool autoSummary)
    {
        var ask = new FakeAsk { BookLanguages = ["en", "zh"] };
        var translator = new FakeTranslator();
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask, () => autoSummary);
        var vm = Page(ask, coordinator, translator, new AppSettings { SearchOtherLanguages = searchOtherLanguages, AutoSummary = autoSummary });

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Empty(translator.Calls);
        Assert.False(vm.Latest!.HasAlsoSearched);
    }

    [Fact]
    public async Task Ask_TheModelIsUnavailable_TheQuestionIsSearchedAlone_AndThePageSaysSo()
    {
        var ask = new FakeAsk { BookLanguages = ["en", "zh"] };
        var translator = new FakeTranslator { OnTranslate = (_, _) => throw new TranslationUnavailableException() };
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = Page(ask, coordinator, translator);

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("Ask_WarningNoTranslation", vm.Latest!.Warning);
        Assert.False(vm.Latest.HasAlsoSearched);
        Assert.Single(vm.Turns);                                       // the search went on
        Assert.Empty(ask.AlsoSearches.Single() ?? []);
    }

    [Fact]
    public async Task QuestionTranslator_ACopyOfTheQuestion_IsEmpty_ButAReplyWithNoUsableLineIsAFailure()
    {
        var copy = new QuestionTranslator(new ScriptedChatModel("Chinese: Plato"));
        Assert.Empty(await copy.TranslateAsync("Plato", ["zh"], default));

        var garbage = new QuestionTranslator(new ScriptedChatModel("   \n"));
        await Assert.ThrowsAsync<TranslationUnavailableException>(() => garbage.TranslateAsync("Plato", ["zh"], default));
        var preamble = new QuestionTranslator(new ScriptedChatModel("Sure, here you go:\nChinese：柏拉图\nJapanese: プラトン"));
        Assert.Equal(["柏拉图", "プラトン"], await preamble.TranslateAsync("Plato", ["zh", "ja"], default));
    }

    [Fact]
    public async Task Ask_AQuestionWithNoCommonWords_IsStillTranslated()
    {
        var ask = new FakeAsk { BookLanguages = ["en", "zh"] };
        var translator = new FakeTranslator { OnTranslate = (_, _) => [Chinese] };    // the English copy was dropped by the parser
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = Page(ask, coordinator, translator, question: "Why does Darcy love Elizabeth?");

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(["en", "zh"], Assert.Single(translator.Calls).Languages);          // a library of two languages: both, the question's own included
        Assert.Equal([Chinese], ask.AlsoSearches.Single());
        Assert.True(vm.Latest!.HasAlsoSearched);
    }

    [Fact]
    public async Task Ask_ACopyOfTheQuestionCameBack_IsNotAFailure_NoWarningAndNoChip()
    {
        var ask = new FakeAsk { BookLanguages = ["en", "zh"] };
        var translator = new FakeTranslator { OnTranslate = (_, _) => [] };      // a name or a number is the same in every language
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = Page(ask, coordinator, translator, question: "Plato");

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Null(vm.Latest!.Warning);
        Assert.False(vm.Latest.HasAlsoSearched);
    }

    [Fact]
    public async Task Ask_Retry_AfterAFailedTranslation_TranslatesAgain_AndTheWarningGoes()
    {
        var ask = new FakeAsk { BookLanguages = ["en", "zh"] };
        var translator = new FakeTranslator { OnTranslate = (_, _) => throw new TranslationUnavailableException() };
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = Page(ask, coordinator, translator);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("Ask_WarningNoTranslation", vm.Latest!.Warning);
        translator.OnTranslate = (_, _) => [Chinese];

        vm.Latest.RetryCommand.Execute(null);
        await Eventually(() => vm.Latest is { IsSearching: false, HasAlsoSearched: true });

        Assert.Equal(2, translator.Calls.Count);
        Assert.Null(vm.Latest.Warning);
    }

    [Fact]
    public async Task Ask_ASuccessfulTranslation_HasNoWarning_AndOneLanguageHasNoCall()
    {
        var ask = new FakeAsk { BookLanguages = ["en", "zh"] };
        var translator = new FakeTranslator { OnTranslate = (_, _) => [Chinese] };
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = Page(ask, coordinator, translator, question: "Why does Darcy love Elizabeth?");
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Null(vm.Latest!.Warning);

        var single = new FakeAsk { BookLanguages = ["en", "en"] };
        var again = new FakeTranslator();
        var vm2 = Page(single, coordinator, again, question: "Why does Darcy love Elizabeth?");
        await vm2.SearchCommand.ExecuteAsync(null);
        Assert.Empty(again.Calls);
        Assert.Null(vm2.Latest!.Warning);
    }

    [Fact]
    public async Task Ask_Retry_KeepsTheTranslations_ButRedoesAFailedOne()
    {
        var ask = new FakeAsk { BookLanguages = ["en", "zh"] };
        var translator = new FakeTranslator { OnTranslate = (_, _) => [Chinese] };
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = Page(ask, coordinator, translator);
        await vm.SearchCommand.ExecuteAsync(null);

        vm.Latest!.RetryCommand.Execute(null);
        await Eventually(() => ask.Searches.Count == 2 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.Single(translator.Calls);                               // not asked again
        Assert.Equal([Chinese], ask.AlsoSearches[1]);

        translator.OnTranslate = (_, _) => throw new TranslationUnavailableException();
        var failing = Page(new FakeAsk { BookLanguages = ["en", "zh"] }, coordinator, translator);
        await failing.SearchCommand.ExecuteAsync(null);
        translator.OnTranslate = (_, _) => [Chinese];
        failing.Latest!.RetryCommand.Execute(null);
        await Eventually(() => failing.Latest is { IsSearching: false, HasAlsoSearched: true });

        Assert.Equal(3, translator.Calls.Count);                       // the failed one was tried again
    }

    [Fact]
    public async Task Ask_ContextChipRemoved_TheTypedQuestionIsTranslatedAgain_NotTheRewrittenOne()
    {
        var ask = new FakeAsk { BookLanguages = ["en", "zh"] };
        var rewriter = new FakeRewriter { OnRewrite = (_, _) => "Who is Elizabeth Bennet's sister?" };
        var translator = new FakeTranslator { OnTranslate = (q, _) => [$"{q} [zh]"] };
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = new AskViewModel(ask, new Strings(), coordinator, new Settings(), new InMemoryHistory(), rewriter: rewriter, translator: translator)
        { Question = "Who is Elizabeth Bennet in the novel" };
        await vm.SearchCommand.ExecuteAsync(null);
        vm.Question = "and her sister?";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("Who is Elizabeth Bennet's sister? [zh]", Assert.Single(vm.Latest!.AlsoSearched));

        vm.Latest.RemoveContextCommand.Execute(null);
        await Eventually(() => ask.Searches.Count == 3 && vm.Latest is { IsSearching: false, EntryId: > 0 });

        Assert.Equal("and her sister?", translator.Calls[^1].Question);
        Assert.Equal(["and her sister? [zh]"], ask.AlsoSearches[2]);
        Assert.Equal(["and her sister? [zh]"], vm.Latest.AlsoSearched);
    }

    [Fact]
    public async Task Search_AQuestionWithTrailingSpace_AndItsIdenticalTranslation_IsSearchedOnce()
    {
        Library();
        var embedder = new FakeQueryEmbedder(_ => TestDb.Vec(1));

        await Service(embedder).SearchAsync(English + " ", alsoSearch: [English]);

        Assert.Equal(1, embedder.Calls);
    }

    [Fact]
    public async Task Ask_ARestoredTurn_ShowsWhatItWasAlsoSearchedAs()
    {
        var history = new InMemoryHistory();
        var settings = new Settings();
        var thread = history.AddThread();
        history.Add(English, SummaryStatus.Ready, threadId: thread, alsoSearched: [Chinese]);
        settings.Save(settings.Load() with { CurrentThreadId = thread });
        var ask = new FakeAsk();
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);
        var vm = new AskViewModel(ask, new Strings(), coordinator, settings, history, a => a());

        await vm.RestoreAsync();

        var turn = vm.Turns.Single();
        Assert.True(turn.HasAlsoSearched);
        Assert.Equal([Chinese], turn.AlsoSearched);
        Assert.Empty(ask.Searches);
    }

    [Fact]
    public void Settings_SearchOtherLanguages_IsOnByDefault_AndSaved()
    {
        Assert.True(new AppSettings().SearchOtherLanguages);
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { SearchOtherLanguages = false });
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.SearchOtherLanguages);
    }
}
