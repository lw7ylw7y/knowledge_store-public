using BookSearch.App;
using BookSearch.App.Ask;
using BookSearch.App.History;
using BookSearch.Core.Chunking;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using static BookSearch.Tests.SummaryKit;

namespace BookSearch.Tests;

/// <summary>The text the model reads for one result (ST-34): the chunk, cut around the question's words, with the sentences before and after it.</summary>
public class ContextTests : IDisposable
{
    private static readonly ISentenceSplitter Splitter = new PunctuationSentenceSplitter();
    private static readonly ITokenizer Words = new WordCountTokenizer();
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A book of chunks where each repeats the last sentence of the one before, like the real chunker. Chunk i holds sentences
    /// "Sentence i-a ...", "Sentence i-b ...", "Sentence i-c ..."; the first of chunk i+1 is the last of chunk i.
    /// </summary>
    private static Dictionary<int, Chunk> Book(int chunks, long bookId = 1, long sectionId = 1)
    {
        var map = new Dictionary<int, Chunk>();
        for (var i = 0; i < chunks; i++)
        {
            var first = i == 0 ? $"Chunk {i} opens the section here." : $"Chunk {i - 1} closes with this carried over sentence.";
            map[i] = new Chunk
            {
                Id = 1000 + i, BookId = bookId, SectionId = sectionId, ChunkIndex = i, TokenCount = 20,
                Text = $"{first} Chunk {i} says something in its middle part. Chunk {i} closes with this carried over sentence.",
            };
        }
        // Make the carried-over sentence identical in the next chunk.
        for (var i = 1; i < chunks; i++)
            map[i] = new Chunk
            {
                Id = map[i].Id, BookId = bookId, SectionId = sectionId, ChunkIndex = i, TokenCount = 20,
                Text = $"Chunk {i - 1} closes with this carried over sentence. Chunk {i} says something in its middle part. Chunk {i} closes with this carried over sentence.",
            };
        return map;
    }

    private sealed class FakeSource(Dictionary<int, Chunk> chunks) : IChunkSource
    {
        public List<(int From, int To)> Calls { get; } = [];
        public Task<IReadOnlyList<Chunk>> GetRunAsync(long bookId, long? sectionId, int fromIndex, int toIndex, CancellationToken ct)
        {
            Calls.Add((fromIndex, toIndex));
            return Task.FromResult<IReadOnlyList<Chunk>>(chunks.Values.Where(c => c.BookId == bookId && c.SectionId == sectionId
                && c.ChunkIndex >= fromIndex && c.ChunkIndex <= toIndex).OrderBy(c => c.ChunkIndex).ToList());
        }
    }

    private static SearchHit HitAt(Chunk chunk, string book = "Pride", string? section = "Chapter 1", int? page = null) =>
        new(chunk.Id, chunk.BookId, book, "Austen", section, page, page, null, "excerpt", [], chunk.Text, 0.03, 0.8, 1, 1, null, null,
            chunk.ChunkIndex, chunk.SectionId);

    private static ContextBuilder Builder(IChunkSource? source, SummaryOptions? options = null) =>
        new(Words, Splitter, options ?? new SummaryOptions(), source);

    private static readonly QueryTerms Terms = QueryTerms.Extract("middle part");

    // ---- the text around a result -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Contexts_GetTextFromBeforeAndAfter_WithinTheTokenBudget()
    {
        var book = Book(10);
        var source = new FakeSource(book);

        var context = await Builder(source).BuildAsync(HitAt(book[5]), Terms);

        Assert.StartsWith("… ", context.Text);                                   // there is more before
        Assert.EndsWith(" …", context.Text);                                     // and after
        Assert.Contains("Chunk 4 closes with this carried over sentence.", context.Text);   // the end of the chunk before
        Assert.Contains("Chunk 5 says something in its middle part.", context.Text);
        Assert.Contains("Chunk 6 says something in its middle part.", context.Text);        // the start of the chunk after
        Assert.DoesNotContain("Chunk 3 says", context.Text);
        Assert.Equal([(4, 6)], source.Calls);
    }

    [Fact]
    public async Task Contexts_WithoutNeighbourTokensOrWithoutASource_AreJustTheResult()
    {
        var book = Book(10);
        var none = new SummaryOptions { NeighborTokens = 0 };

        var noTokens = await Builder(new FakeSource(book), none).BuildAsync(HitAt(book[5]), Terms);
        var noSource = await Builder(null).BuildAsync(HitAt(book[5]), Terms);

        Assert.Equal(book[5].Text, noTokens.Text);
        Assert.Equal(book[5].Text, noSource.Text);
    }

    [Fact]
    public async Task Contexts_ChunksAtTheStartOfASection_HaveNothingBeforeThem()
    {
        var book = Book(10);

        var context = await Builder(new FakeSource(book)).BuildAsync(HitAt(book[0]), Terms);

        Assert.False(context.Text.StartsWith("… ", StringComparison.Ordinal));
        Assert.EndsWith(" …", context.Text);
    }

    [Fact]
    public async Task Contexts_ATooLongCore_IsCutAroundTheMatch_AndGetsNoNeighbourOnTheCutSides()
    {
        var sentences = Enumerable.Range(0, 80).Select(i => i == 40 ? "The middle part is here now." : $"Plain sentence number {i} says nothing useful at all.");
        var chunk = new Chunk { Id = 5, BookId = 1, SectionId = 1, ChunkIndex = 5, Text = string.Join(" ", sentences), TokenCount = 700 };
        var book = Book(10);
        book[5] = chunk;
        var options = new SummaryOptions { TokensPerPassage = 100, NeighborTokens = 50 };

        var context = await Builder(new FakeSource(book), options).BuildAsync(HitAt(chunk), Terms);

        Assert.Contains("The middle part is here now.", context.Text);
        Assert.InRange(context.Tokens, 80, 120);
        Assert.DoesNotContain("Chunk 4", context.Text);                          // cut at the start: no text from the chunk before
        Assert.DoesNotContain("Chunk 6", context.Text);
    }

    [Theory]
    [InlineData(SummaryContextLevel.Small, 200, 0)]
    [InlineData(SummaryContextLevel.Medium, 350, 100)]
    [InlineData(SummaryContextLevel.Large, 500, 200)]
    public void Options_ForLevel_SetsTheBudgets(SummaryContextLevel level, int perPassage, int neighbors)
    {
        var options = SummaryOptions.ForLevel(level);

        Assert.Equal(perPassage, options.TokensPerPassage);
        Assert.Equal(neighbors, options.NeighborTokens);
    }

    // ---- the prompt -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Prompt_IsTrimmedAroundEveryQuestionWord_NotOnlyTheHighlightedOnes()
    {
        // A long passage whose only question-word match is "needle"; the highlight list (rare words) leaves it out.
        var sentences = Enumerable.Range(0, 12).Select(i => i == 9 ? "The needle is hidden here now." : $"Filler sentence number {i} here.");
        var chunk = new Chunk { Id = 1, BookId = 1, SectionId = 1, ChunkIndex = 0, TokenCount = 60, Text = string.Join(" ", sentences) };
        var chat = new ScriptedChatModel("Something.");
        var options = new SummaryOptions { TokensPerPassage = 15, NeighborTokens = 0 };
        var service = new SummaryService(chat, Words, Splitter, options, new FakeSource(new Dictionary<int, Chunk> { [0] = chunk }));
        var search = new SearchResult([HitAt(chunk)], false, false, null, 0.8, false,
            QueryTerms.Extract("needle"), new SearchTimings(0, 0, 0, 0, 0, 0), CentralTerms: new QueryTerms([], []));

        await service.SummarizeAsync("which thing is hidden", search);   // the question itself must not contain the word

        Assert.Contains("needle", chat.Prompts[0][1].Content);
    }

    [Fact]
    public void Settings_SummaryContext_IsSavedAndClamped()
    {
        var store = new Settings();
        var vm = new SettingsViewModel(new AppPaths("/tmp/x"), store, BookSearch.Models.Management.ModelTier.Minimum, new LibraryStatus(0, "v", 5),
            new Strings(), Coordinator(new FakeSummarizer(), new InMemoryHistory()), new Dialogs());
        Assert.Equal(1, vm.SummaryContextIndex);                                  // medium by default
        Assert.Equal(["Settings_ContextSmall", "Settings_ContextMedium", "Settings_ContextLarge"], vm.ContextChoices);

        vm.SummaryContextIndex = 2;
        Assert.Equal(2, store.Load().SummaryContext);
        vm.SummaryContextIndex = 7;                                               // not a choice: ignored
        Assert.Equal(2, store.Load().SummaryContext);
    }

    // ---- reading the chunks around a result -------------------------------------------------------------------------------

    [Fact]
    public void Chunks_GetRun_ReturnsTheIndexRangeOfOneSectionInOrder()
    {
        var book = _db.AddBook("Emma");
        var s1 = _db.Sections.Insert(new Section { BookId = book, Title = "One", Order = 1 });
        var s2 = _db.Sections.Insert(new Section { BookId = book, Title = "Two", Order = 2 });
        for (var i = 0; i < 6; i++)
            _db.Add(new ChunkWrite(new Chunk { BookId = book, SectionId = i < 4 ? s1 : s2, ChunkIndex = i, Text = $"chunk {i}", TokenCount = 5 }, TestDb.Vec(1)));

        var run = _db.Chunks.GetRun(book, s1, 1, 5);

        Assert.Equal([1, 2, 3], run.Select(c => c.ChunkIndex));                   // chunks 4 and 5 belong to the next section
        Assert.Equal(["chunk 1", "chunk 2", "chunk 3"], run.Select(c => c.Text));
    }

    private static int Count(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
