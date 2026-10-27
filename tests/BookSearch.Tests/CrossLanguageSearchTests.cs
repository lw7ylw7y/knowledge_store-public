using BookSearch.Core.Data;
using BookSearch.Core.Search;
using BookSearch.Parsing;
using Microsoft.Data.Sqlite;

namespace BookSearch.Tests;

public class CrossLanguageSearchTests : IDisposable
{
    private readonly TestDb _db = new();
    private int _chunk;

    public void Dispose() => _db.Dispose();

    private const string SpanishQuestion = "¿Por qué el caballero de la triste figura lucha con los molinos de viento en la llanura?";
    private const string EnglishQuestion = "Why does the old knight attack the windmills on the plain?";

    /// <summary>A unit vector whose cosine with the question vector (the first axis) is exactly <paramref name="similarity"/>.</summary>
    private static float[] VecWithSimilarity(double similarity, int axis)
    {
        var v = new float[Database.EmbeddingDimension];
        v[0] = (float)similarity;
        v[axis] = (float)Math.Sqrt(1 - similarity * similarity);
        return v;
    }

    private static float[] QuestionVector()
    {
        var v = new float[Database.EmbeddingDimension];
        v[0] = 1;
        return v;
    }

    private long AddBook(string title, string language)
    {
        var id = _db.AddBook(title);
        new SqliteCommand($"UPDATE book SET language = '{language}' WHERE id = {id}", _db.Conn).ExecuteNonQuery();
        return id;
    }

    private void AddChunks(long book, int count, string text, double similarity)
    {
        for (var i = 0; i < count; i++)
        {
            var axis = 1 + _chunk++;
            _db.Add(_db.Write(book, i, $"{text} {i}", VecWithSimilarity(similarity, axis)));
        }
    }

    private SearchService Service(SearchOptions? options) =>
        new(_db.Conn, new FakeQueryEmbedder(_ => QuestionVector()), options);

    [Fact]
    public void TestQuestions_AreDetectedAsExpected()
    {
        Assert.Equal("es", LanguageDetector.Detect(SpanishQuestion));
        Assert.Equal("en", LanguageDetector.Detect(EnglishQuestion));
    }

    // ---- result slots -------------------------------------------------------------------------------------------

    private (long Spanish, long English) SpanishKeywordsEnglishMeaning()
    {
        var spanish = AddBook("El Quijote", "es");
        var english = AddBook("Don Quixote", "en");
        // Ten Spanish passages share the question's words and are fairly close in meaning...
        AddChunks(spanish, 10, "El caballero de la triste figura lucha con los molinos de viento en la llanura", 0.60);
        // ...while three English passages about the same scene share no words but are closer in meaning.
        AddChunks(english, 3, "The knight charges the windmills believing them giants", 0.70);
        return (spanish.Id(), english.Id());
    }

    [Fact]
    public async Task Search_Results_AreTheTopOfTheMergedList_WithNoSlotKeptForOtherLanguages()
    {
        var (spanish, english) = SpanishKeywordsEnglishMeaning();

        var result = await Service(new SearchOptions()).SearchAsync(SpanishQuestion);

        // Ten Spanish passages match the words and are fairly close in meaning, so they fill the three results. The English
        // passages, closer in meaning but with no shared words, are not forced in (ST-27: the slot for them was removed
        // because it showed the best meaning match after weaker ones).
        Assert.Equal(3, result.Hits.Count);
        Assert.All(result.Hits, h => Assert.Equal(spanish, h.BookId));
        Assert.DoesNotContain(result.Hits, h => h.BookId == english);
    }

    [Fact]
    public async Task Search_AMeaningOnlyMatchOutsideTheMergedTop_IsNotShown_ThatIsTheTradeOffOfRemovingTheSlot()
    {
        var meaning = AddBook("Notes on frugality", "en");
        var words = AddBook("Notes on cheap tools", "en");
        AddChunks(meaning, 1, "thrift and a low spending habit in people", 0.80);        // the best by meaning, shares no word with the question
        AddChunks(words, 6, "someone consistently buys the cheapest tools", 0.55);     // shares "someone" and "consistently", weaker in meaning

        var result = await new SearchService(_db.Conn, new FakeQueryEmbedder(_ => QuestionVector()),
            new SearchOptions { TopN = 4, CoverageThreshold = 0 }).SearchAsync("Why would someone consistently buy cheap things?");

        // Merged by rank, passages that both lists like come first: with the meaning-only slot gone (ST-27) the best meaning
        // match is no longer added at the end, and here it falls out of the four results. Weighting the meaning list more
        // (or merging by score) is the way to bring such a passage up, not a slot at the end.
        Assert.Equal(4, result.Hits.Count);
        Assert.All(result.Hits, h => Assert.Equal(words, h.BookId));
    }

    // ---- coverage ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_OtherLanguageMatch_CountsAsCoveredAtTheLowerThreshold()
    {
        var english = AddBook("Don Quixote", "en");
        AddChunks(english, 3, "The knight charges the windmills believing them giants", 0.40);   // 0.35 <= 0.40 < 0.45

        var classic = await Service(SearchOptions.Classic).SearchAsync(SpanishQuestion);
        var current = await Service(new SearchOptions()).SearchAsync(SpanishQuestion);

        Assert.True(classic.NotCovered);                                     // the same-language threshold rejects it
        Assert.False(current.NotCovered);                                    // a cross-language match is accepted
        Assert.Equal(0.40, current.BestSimilarity!.Value, 3);
    }

    [Fact]
    public async Task Search_SameLanguageMatchBelowThreshold_StillNotCovered()
    {
        var english = AddBook("Don Quixote", "en");
        AddChunks(english, 3, "Lorem ipsum dolor sit amet", 0.40);

        var result = await Service(new SearchOptions()).SearchAsync(EnglishQuestion);

        Assert.True(result.NotCovered);                                      // same language: the normal 0.45 still applies
    }

    [Fact]
    public async Task Search_OtherLanguageMatchBelowTheLowerThreshold_StillNotCovered()
    {
        var english = AddBook("Don Quixote", "en");
        AddChunks(english, 3, "Lorem ipsum dolor sit amet", 0.30);

        var result = await Service(new SearchOptions()).SearchAsync(SpanishQuestion);

        Assert.True(result.NotCovered);
    }

    [Fact]
    public async Task Search_UnknownLanguages_UseTheNormalThreshold()
    {
        var unknown = _db.AddBook("No language recorded");                   // book.language is null
        AddChunks(unknown, 3, "Lorem ipsum dolor sit amet", 0.40);

        var result = await Service(new SearchOptions()).SearchAsync(SpanishQuestion);

        Assert.True(result.NotCovered);
    }
}

internal static class LongExtensions
{
    public static long Id(this long id) => id;
}
