using BookSearch.Core;
using BookSearch.Core.Chunking;
using BookSearch.Core.Data;
using BookSearch.Core.Ingest;
using BookSearch.Core.Search;
using BookSearch.Models;

namespace BookSearch.Tests;

/// <summary>Returns a fixed vector (or throws) for every question.</summary>
internal sealed class FakeQueryEmbedder(Func<string, float[]> embed) : IQueryEmbedder
{
    public int Calls { get; private set; }

    public Task<float[]> EmbedQueryAsync(string question, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(embed(question));
    }
}

/// <summary>Bag-of-words feature hashing: similar word overlap gives similar vectors. Good enough to exercise the pipeline.</summary>
internal sealed class HashingEmbedder : IEmbedder, IQueryEmbedder
{
    public string ModelId => "hashing";

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());

    public Task<float[]> EmbedQueryAsync(string question, CancellationToken ct = default) => Task.FromResult(Embed(question));

    private static float[] Embed(string text)
    {
        var v = new float[Database.EmbeddingDimension];
        foreach (var word in QueryTerms.Extract(text, maxWords: 10_000).Words)
        {
            var h = 17;
            foreach (var ch in word) h = unchecked(h * 31 + ch);
            v[Math.Abs(h) % v.Length] += 1;
        }
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }
}

public class SearchTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private long AddChunk(long book, int index, string text, float[] vec, long? section = null, int? page = null, bool cjk = false)
    {
        var c = new Chunk { BookId = book, SectionId = section, ChunkIndex = index, Text = text, TokenCount = 10, PageStart = page, PageEnd = page };
        Database.InTransaction(_db.Conn, tx => _db.Chunks.WriteBatch([new ChunkWrite(c, vec, cjk)], tx));
        return Convert.ToInt64(new Microsoft.Data.Sqlite.SqliteCommand(
            $"SELECT id FROM chunk WHERE book_id = {book} AND chunk_index = {index}", _db.Conn).ExecuteScalar());
    }

    private SearchService Service(Func<string, float[]>? embed, SearchOptions? options = null) =>
        new(_db.Conn, embed is null ? null : new FakeQueryEmbedder(embed), options);

    // ---- fusion -------------------------------------------------------------------------------------------

    [Fact]
    public void Rrf_CombinesRanks_Correctly()
    {
        // 1: 1/61. 2: 1/62 + 1/62. 3: 1/63 + 1/61. 4: 1/63.
        var fused = ReciprocalRankFusion.Fuse([[1L, 2L, 3L], [3L, 2L, 4L]], top: 10, k: 60);

        Assert.Equal([3L, 2L, 1L, 4L], fused.Select(f => f.Id));
        Assert.Equal(1.0 / 63 + 1.0 / 61, fused[0].Score, 10);
        Assert.Equal(2.0 / 62, fused[1].Score, 10);
        Assert.Equal([3, 1], fused[0].Ranks);      // rank 3 in the first list, rank 1 in the second
        Assert.Equal([null, 3], fused[3].Ranks);
    }

    [Fact]
    public void Rrf_ItemInBothLists_RanksAboveSingleList()
    {
        // Item 2 is only second in each list, yet beats the first-place items that appear once.
        var fused = ReciprocalRankFusion.Fuse([[1L, 2L], [3L, 2L]], top: 10);

        Assert.Equal(2, fused[0].Id);
    }

    [Fact]
    public void Rrf_TopLimitsResultsAndTiesAreStable()
    {
        var fused = ReciprocalRankFusion.Fuse([[10L, 20L, 30L], [40L, 50L, 60L]], top: 4);

        Assert.Equal(4, fused.Count);
        Assert.Equal([10L, 40L, 20L, 50L], fused.Select(f => f.Id)); // equal scores keep first-seen order
        Assert.Empty(ReciprocalRankFusion.Fuse([[], []], top: 5));
    }

    // ---- query terms --------------------------------------------------------------------------------------

    [Fact]
    public void QueryTerms_DropStopwordsAndSplitOnPunctuation()
    {
        var terms = QueryTerms.Extract("What did the White Rabbit say about rabbit-holes?!");

        Assert.Equal(["white", "rabbit", "say", "holes"], terms.Words);
        Assert.Equal("\"white\" OR \"rabbit\" OR \"say\" OR \"holes\"", terms.WordMatchExpression);
        Assert.False(terms.HasTrigrams);
        Assert.Null(terms.TrigramMatchExpression);
    }

    [Fact]
    public void QueryTerms_CjkRunsBecomeTrigrams()
    {
        var terms = QueryTerms.Extract("先生と私 and Kokoro");

        Assert.Equal(["kokoro"], terms.Words);
        Assert.Equal(["先生と", "生と私"], terms.Trigrams);
        Assert.Equal("\"先生と\" OR \"生と私\"", terms.TrigramMatchExpression);
    }

    [Fact]
    public void QueryTerms_ThaiAndShortRuns()
    {
        Assert.True(QueryTerms.Extract("ภาษาไทย").HasTrigrams);
        Assert.False(QueryTerms.Extract("先生").HasTrigrams);        // two characters: too short for the trigram index
        Assert.False(QueryTerms.Extract("the of and").HasWords);
    }

    [Fact]
    public void QueryTerms_AreCappedAndDistinct()
    {
        var many = string.Join(" ", Enumerable.Range(0, 100).Select(i => $"word{i}"));

        Assert.Equal(32, QueryTerms.Extract(many).Words.Count);
        Assert.Equal(["cat"], QueryTerms.Extract("Cat cat CAT").Words);
    }

    // ---- highlighting and excerpts ------------------------------------------------------------------------

    [Fact]
    public void Highlight_FindsMatchedTermsIgnoringCase()
    {
        var terms = QueryTerms.Extract("rabbit hole");

        var spans = ExcerptBuilder.FindMatches("The White RABBIT ran down the Rabbit-Hole.", terms);

        Assert.Equal(["RABBIT", "Rabbit", "Hole"], spans.Select(s => "The White RABBIT ran down the Rabbit-Hole."[s.Start..s.End]));
    }

    [Fact]
    public void Highlight_MatchesWholeWordsOnly()
    {
        Assert.Empty(ExcerptBuilder.FindMatches("concatenate", QueryTerms.Extract("cat")));
        Assert.Single(ExcerptBuilder.FindMatches("a cat's tail", QueryTerms.Extract("cat")));
    }

    [Fact]
    public void Highlight_CjkTrigramsMergeIntoOneSpan()
    {
        const string text = "私はその人を常に先生と私は呼んでいた。";
        var spans = ExcerptBuilder.FindMatches(text, QueryTerms.Extract("先生と私"));

        var span = Assert.Single(spans);
        Assert.Equal("先生と私", text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Excerpt_CentersOnDenseMatchesAndHighlightsAreRelative()
    {
        var filler = string.Join(" ", Enumerable.Range(0, 80).Select(i => $"filler{i}"));
        var passage = $"{filler} the rabbit found a hole near the rabbit warren. {filler}";

        var (excerpt, highlights) = ExcerptBuilder.Build(passage, QueryTerms.Extract("rabbit hole"), 200);

        Assert.True(excerpt.Length <= 210, $"excerpt is {excerpt.Length} chars");
        Assert.StartsWith("… ", excerpt);
        Assert.EndsWith(" …", excerpt);
        Assert.Equal(["rabbit", "hole", "rabbit"], highlights.Select(h => excerpt.Substring(h.Start, h.Length)));
    }

    [Fact]
    public void Excerpt_WithoutMatches_StartsAtTheBeginning()
    {
        var passage = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"w{i}"));

        var (excerpt, highlights) = ExcerptBuilder.Build(passage, QueryTerms.Extract("zebra"), 100);

        Assert.StartsWith("w0 w1", excerpt);
        Assert.Empty(highlights);
        Assert.EndsWith(" …", excerpt);
    }

    [Fact]
    public void Excerpt_ShortPassage_IsReturnedWhole()
    {
        var (excerpt, highlights) = ExcerptBuilder.Build("A short\npassage about\n\nrabbits.", QueryTerms.Extract("passage"), 320);

        Assert.Equal("A short passage about rabbits.", excerpt);
        Assert.Equal("passage", excerpt.Substring(highlights[0].Start, highlights[0].Length));
    }

    // ---- search service -----------------------------------------------------------------------------------

    [Fact]
    public async Task Search_BelowThreshold_ReturnsNotCovered()
    {
        var book = _db.AddBook("Cooking");
        AddChunk(book, 0, "Whisk the eggs and fold in the flour.", TestDb.Vec(1, 0));
        AddChunk(book, 1, "Bake until golden brown.", TestDb.Vec(0.9f, 0.1f));

        // The question points in an orthogonal direction: similarity 0, far below the 0.45 threshold.
        var result = await Service(_ => TestDb.Vec(0, 0, 1)).SearchAsync("quantum chromodynamics");

        Assert.True(result.NotCovered);
        Assert.False(result.KeywordOnly);
        Assert.Equal(0.0, result.BestSimilarity!.Value, 5);
        Assert.NotEmpty(result.Hits);                    // the nearest passages are still listed
    }

    [Fact]
    public async Task Search_AboveThreshold_IsCovered()
    {
        var book = _db.AddBook("Cooking");
        AddChunk(book, 0, "Whisk the eggs and fold in the flour.", TestDb.Vec(1, 0));
        AddChunk(book, 1, "Bake until golden brown.", TestDb.Vec(0, 1));

        var result = await Service(_ => TestDb.Vec(1, 0.1f)).SearchAsync("how do I make batter");

        Assert.False(result.NotCovered);
        Assert.True(result.BestSimilarity > 0.9);
        Assert.Equal("Whisk the eggs and fold in the flour.", result.Hits[0].Text);
        Assert.Equal(1, result.Hits[0].VectorRank);
        Assert.NotNull(result.Hits[0].Similarity);
    }

    // ---- central words (only rare words are highlighted) ----------------------------------------------------

    /// <summary>A library of 60 chunks: every one says "people"; chunk 0 also has "zebra"; chunks 0-2 have "stripes".</summary>
    private void AddEverydayWordLibrary()
    {
        var book = _db.AddBook("Animals");
        for (var i = 0; i < 60; i++)
        {
            var extra = i == 0 ? " The zebra has stripes." : i < 3 ? " Some stripes are faint." : "";
            AddChunk(book, i, $"Many people walk past the farm on day {i}.{extra}", i == 0 ? TestDb.Vec(1, 0) : TestDb.Vec(0, 1));
        }
    }

    [Fact]
    public async Task Search_Highlights_OnlyWordsRareInTheLibrary()
    {
        AddEverydayWordLibrary();

        var result = await Service(_ => TestDb.Vec(1, 0)).SearchAsync("people zebra");

        Assert.Equal(["people", "zebra"], result.Terms.Words);                  // the search still uses every word
        Assert.Equal(["zebra"], result.CentralTerms!.Words);
        var zebra = result.Hits.First(h => h.Text.Contains("zebra"));
        Assert.Equal(["zebra"], zebra.Highlights.Select(h => zebra.Excerpt.Substring(h.Start, h.Length)));
        Assert.All(result.Hits, h => Assert.DoesNotContain("people", h.Highlights.Select(x => h.Excerpt.Substring(x.Start, x.Length))));
    }

    [Fact]
    public async Task Search_Highlights_AllCommonWords_MarkNothingButStillFindResults()
    {
        AddEverydayWordLibrary();

        var result = await Service(_ => TestDb.Vec(1, 0)).SearchAsync("people farm walk");

        Assert.NotEmpty(result.Hits);
        Assert.Empty(result.CentralTerms!.Words);
        Assert.Equal(["people", "farm", "walk"], result.Terms.Words);
        Assert.All(result.Hits, h => Assert.Empty(h.Highlights));
    }

    [Fact]
    public async Task Search_Highlights_ShareAndFloorDecideWhatIsRare()
    {
        AddEverydayWordLibrary();

        // "stripes" is in 3 of 60 chunks: under the floor of 5, so central; at a 2% share and a floor of 1 it is not.
        var floor = await Service(_ => TestDb.Vec(1, 0)).SearchAsync("stripes");
        var strict = await Service(_ => TestDb.Vec(1, 0), new SearchOptions { CentralTermMinChunks = 1 }).SearchAsync("stripes");

        Assert.Equal(["stripes"], floor.CentralTerms!.Words);
        Assert.Empty(strict.CentralTerms!.Words);
    }

    [Fact]
    public async Task Search_Highlights_TinyLibrary_MarksEveryQuestionWord()
    {
        var book = _db.AddBook("Tiny");
        AddChunk(book, 0, "People like zebras.", TestDb.Vec(1, 0));
        AddChunk(book, 1, "People like horses.", TestDb.Vec(0, 1));

        var result = await Service(_ => TestDb.Vec(1, 0)).SearchAsync("people zebras");

        Assert.Equal(["people", "zebras"], result.CentralTerms!.Words);          // too little data to call a word common
    }

    [Fact]
    public async Task Search_Highlights_CjkTrigramsFollowTheSameRule()
    {
        var book = _db.AddBook("日本");
        for (var i = 0; i < 60; i++)
            AddChunk(book, i, i == 0 ? "吾輩は猫である。名前はまだ無い。" : "吾輩は毎日歩いている。", TestDb.Vec(1, 0), cjk: true);

        var result = await Service(_ => TestDb.Vec(1, 0)).SearchAsync("吾輩は猫");

        Assert.Contains("吾輩は", result.Terms.Trigrams);
        Assert.DoesNotContain("吾輩は", result.CentralTerms!.Trigrams);           // in every chunk
        Assert.Contains("輩は猫", result.CentralTerms.Trigrams);                  // in one
    }

    [Fact]
    public void Excerpt_OnlyHighlightTermsAreMarked_ButAllTermsPlaceTheCut()
    {
        var filler = string.Join(" ", Enumerable.Range(0, 80).Select(i => $"filler{i}"));
        var passage = $"people {filler} people people zebra near the people. {filler}";

        var all = QueryTerms.Extract("people zebra");
        var (excerpt, highlights) = ExcerptBuilder.Build(passage, all, 120, new QueryTerms(["zebra"], []));

        Assert.StartsWith("… ", excerpt);                                        // cut around the cluster of all matches
        Assert.Equal(["zebra"], highlights.Select(h => excerpt.Substring(h.Start, h.Length)));
    }

    [Fact]
    public void SearchResult_HighlightTerms_FallBackToAllTerms()
    {
        var terms = new QueryTerms(["a1", "b2"], []);
        var plain = new SearchResult([], false, false, null, null, false, terms, new SearchTimings(0, 0, 0, 0, 0, 0));
        var central = plain with { CentralTerms = new QueryTerms(["b2"], []) };

        Assert.Same(terms, plain.HighlightTerms);
        Assert.Equal(["b2"], central.HighlightTerms.Words);
    }

    [Fact]
    public async Task Search_ExactKeywordMatchOverridesLowSimilarity()
    {
        var book = _db.AddBook("Pride");
        AddChunk(book, 0, "Mr. Bingley was good-looking and gentlemanlike.", TestDb.Vec(1, 0));
        AddChunk(book, 1, "Unrelated text about farming.", TestDb.Vec(0, 1));

        // A name's vector can sit far from its passage; the exact keyword hit keeps it "covered".
        var named = await Service(_ => TestDb.Vec(0, 0, 1)).SearchAsync("Bingley");
        var absent = await Service(_ => TestDb.Vec(0, 0, 1)).SearchAsync("Darcy");

        Assert.False(named.NotCovered);
        Assert.Equal(1, named.Hits[0].KeywordRank);
        Assert.True(absent.NotCovered);
    }

    [Fact]
    public async Task Search_Hybrid_ChunkFoundByBothRetrieversRanksFirst()
    {
        var book = _db.AddBook("Mixed");
        AddChunk(book, 0, "Rabbits dig burrows underground.", TestDb.Vec(0.9f, 0.1f));          // vector rank 2, keyword rank 1
        AddChunk(book, 1, "The weather was cold and rainy today.", TestDb.Vec(1, 0));            // vector rank 1 only
        AddChunk(book, 2, "Hares and rabbits are not the same animal.", TestDb.Vec(0, 0, 1));    // keyword rank 2 only
        var both = _db.Chunks.FtsSearch("\"rabbits\"", 5);
        Assert.Equal(2, both.Count);

        var result = await Service(_ => TestDb.Vec(1, 0)).SearchAsync("rabbits");

        Assert.Equal(3, result.Hits.Count);
        var top = result.Hits[0];
        Assert.Equal("Rabbits dig burrows underground.", top.Text);
        Assert.NotNull(top.VectorRank);
        Assert.NotNull(top.KeywordRank);
        Assert.Contains(result.Hits.Skip(1), h => h.VectorRank is null != (h.KeywordRank is null));
    }

    [Fact]
    public async Task Search_Hits_CarryBookSectionPageAndHighlights()
    {
        var book = _db.AddBook("The Origin");
        _db.Books.SetExtracted(book, "On the Origin of Species", "Charles Darwin", "en", "m");
        var section = _db.Sections.Insert(new Section { BookId = book, Title = "Chapter IV", Order = 0 });
        AddChunk(book, 0, "Natural selection acts solely by accumulating slight variations.", TestDb.Vec(1), section, page: 5);

        var hit = (await Service(_ => TestDb.Vec(1)).SearchAsync("natural selection")).Hits.Single();

        Assert.Equal(("On the Origin of Species", "Charles Darwin", "Chapter IV"), (hit.BookTitle, hit.Author, hit.SectionTitle));
        Assert.Equal((5, 5), (hit.PageStart, hit.PageEnd));
        Assert.Equal(["Natural", "selection"], hit.Highlights.Select(h => hit.Excerpt.Substring(h.Start, h.Length)));
    }

    [Fact]
    public async Task Search_EmbeddingDown_FallsBackToKeyword()
    {
        var book = _db.AddBook("Alice");
        AddChunk(book, 0, "Alice fell down the rabbit hole.", TestDb.Vec(1));
        AddChunk(book, 1, "The Queen shouted off with her head.", TestDb.Vec(0, 1));
        var down = new FakeQueryEmbedder(_ => throw new ModelUnavailableException("connection refused"));

        var result = await new SearchService(_db.Conn, down).SearchAsync("rabbit hole");

        Assert.True(result.KeywordOnly);
        Assert.Contains("keyword", result.Warning);
        Assert.False(result.NotCovered);
        Assert.Equal("Alice fell down the rabbit hole.", result.Hits.Single().Text);
        Assert.Null(result.Hits[0].VectorRank);
        Assert.Null(result.BestSimilarity);
    }

    [Fact]
    public async Task Search_NoEmbedder_IsKeywordOnlyAndNoMatchesIsNotCovered()
    {
        var book = _db.AddBook("Alice");
        AddChunk(book, 0, "Alice fell down the rabbit hole.", TestDb.Vec(1));

        var service = new SearchService(_db.Conn, null);

        Assert.False((await service.SearchAsync("rabbit")).NotCovered);
        var none = await service.SearchAsync("quantum");
        Assert.True(none.KeywordOnly);
        Assert.True(none.NotCovered);
        Assert.Empty(none.Hits);
    }

    [Fact]
    public async Task Search_Cancellation_IsNotSwallowedAsFallback()
    {
        var book = _db.AddBook("Alice");
        AddChunk(book, 0, "Alice fell down the rabbit hole.", TestDb.Vec(1));
        var cancelled = new FakeQueryEmbedder(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SearchService(_db.Conn, cancelled).SearchAsync("rabbit"));
    }

    [Fact]
    public async Task Search_CjkQuestion_QueriesTrigramIndex()
    {
        var book = _db.AddBook("こころ");
        AddChunk(book, 0, "私はその人を常に先生と呼んでいた。", TestDb.Vec(1), cjk: true);
        AddChunk(book, 1, "Unrelated English text.", TestDb.Vec(0, 1));

        var result = await new SearchService(_db.Conn, null).SearchAsync("先生と呼んでいた人");

        Assert.True(result.QueriedTrigramIndex);
        var hit = Assert.Single(result.Hits);
        Assert.Equal(1, hit.TrigramRank);
        Assert.Contains("先生", hit.Excerpt);
        Assert.NotEmpty(hit.Highlights);
    }

    [Fact]
    public async Task Search_LatinQuestion_SkipsTrigramIndex()
    {
        var book = _db.AddBook("Alice");
        AddChunk(book, 0, "Alice fell down the rabbit hole.", TestDb.Vec(1));

        var result = await new SearchService(_db.Conn, null).SearchAsync("rabbit hole");

        Assert.False(result.QueriedTrigramIndex);
        Assert.All(result.Hits, h => Assert.Null(h.TrigramRank));
    }

    [Fact]
    public async Task Search_EmptyLibraryAndBlankQuestion()
    {
        var empty = await Service(_ => TestDb.Vec(1)).SearchAsync("anything at all");

        Assert.True(empty.NotCovered);
        Assert.Empty(empty.Hits);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(_ => TestDb.Vec(1)).SearchAsync("   "));
    }

    [Fact]
    public async Task Search_ReportsTimingsAndHonoursTopN()
    {
        var book = _db.AddBook("Many");
        for (var i = 0; i < 20; i++) AddChunk(book, i, $"Passage number {i} about rabbits.", TestDb.Vec(1, i * 0.01f));

        var result = await Service(_ => TestDb.Vec(1), new SearchOptions { TopN = 5 }).SearchAsync("rabbits");

        Assert.Equal(5, result.Hits.Count);
        Assert.True(result.Timings.Total > 0);
        Assert.True(result.Timings.Total >= result.Timings.Vector);
    }

    // ---- end to end on a real book ------------------------------------------------------------------------

    [Fact]
    public async Task Search_AliceRabbitHole_TopResultFromChapter1()
    {
        var embedder = new HashingEmbedder();
        var ingest = new IngestService(_db.Conn, embedder, new WordCountTokenizer(), new PunctuationSentenceSplitter());
        var added = await ingest.AddAsync(Fixture("alice.epub"));
        await ingest.IngestAsync(added.BookId);

        // Word-overlap vectors are only a stand-in for the real model, so don't apply the coverage threshold here.
        // "rabbit" is in 23 of this 80-chunk book's chunks, so the share rule would not call it central; widen the floor
        // so this test (which is about ranking) still sees it highlighted.
        var service = new SearchService(_db.Conn, embedder, new SearchOptions { CoverageThreshold = 0, CentralTermMinChunks = 30 });
        var result = await service.SearchAsync("falling down the rabbit hole");

        Assert.Equal(3, result.Hits.Count);
        const string chapter1 = "CHAPTER I. Down the Rabbit-Hole";
        // The best keyword match is the chapter I passage about the rabbit-hole...
        Assert.Equal(chapter1, result.Hits.First(h => h.KeywordRank == 1).SectionTitle);
        // ...and chapter I fills the next places. (The book's short Contents page, which lists "Down the Rabbit-Hole",
        // also scores highly with these stand-in vectors; see Implementation notes. The real-model test below checks the same.)
        Assert.Equal(2, result.Hits.Take(3).Count(h => h.SectionTitle == chapter1));
        Assert.All(result.Hits, h => Assert.Equal("Alice's Adventures in Wonderland", h.BookTitle));
        var top = result.Hits.First(h => h.SectionTitle == chapter1);
        Assert.Contains(top.Highlights, h => top.Excerpt.Substring(h.Start, h.Length).Equals("rabbit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_CharacterName_KeywordMatchRanksFirstAndIsCovered()
    {
        var embedder = new HashingEmbedder();
        var ingest = new IngestService(_db.Conn, embedder, new WordCountTokenizer(), new PunctuationSentenceSplitter());
        foreach (var book in new[] { "alice.epub", "pride.epub" })
            await ingest.IngestAsync((await ingest.AddAsync(Fixture(book))).BookId);

        var result = await new SearchService(_db.Conn, embedder).SearchAsync("Bingley");

        Assert.False(result.NotCovered);
        Assert.Equal("Pride and Prejudice", result.Hits[0].BookTitle);
        Assert.Contains("Bingley", result.Hits[0].Text);
        Assert.NotNull(result.Hits[0].KeywordRank);
        Assert.All(result.Hits, h => Assert.Contains("Bingley", h.Text)); // every hit actually mentions him
    }

    // ---- real model (skipped unless BOOKSEARCH_M0_CONFIG is set) -----------------------------------------

    [SkippableFact]
    public async Task Search_ParaphrasedQuestion_FindsPassageWithoutKeywords()
    {
        var path = Environment.GetEnvironmentVariable("BOOKSEARCH_M0_CONFIG");
        Skip.If(string.IsNullOrEmpty(path), "BOOKSEARCH_M0_CONFIG not set");

        await using var embedding = new LazyEmbeddingServer(M0Config.Load(path!));
        var ingest = new IngestService(_db.Conn, embedding, embedding);
        await ingest.IngestAsync((await ingest.AddAsync(Fixture("alice.epub"))).BookId);

        // Mostly different words from the passage where Alice tumbles after the White Rabbit.
        var result = await new SearchService(_db.Conn, embedding)
            .SearchAsync("a young girl plunges into a deep burrow after a hurrying creature");

        Assert.Contains(result.Hits.Take(5), h => h.SectionTitle == "CHAPTER I. Down the Rabbit-Hole");
    }
}
