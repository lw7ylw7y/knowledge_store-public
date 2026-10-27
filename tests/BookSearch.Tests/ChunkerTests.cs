using BookSearch.Core.Chunking;
using BookSearch.Parsing;

namespace BookSearch.Tests;

/// <summary>One token per whitespace-separated word, so chunk sizes in the tests are exact.</summary>
internal sealed class WordCountTokenizer : ITokenizer
{
    public IReadOnlyList<int> Count(IReadOnlyList<string> texts) =>
        texts.Select(t => t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length).ToList();
}

public class ChunkerTests
{
    private static readonly ISentenceSplitter Punctuation = new PunctuationSentenceSplitter();

    private static Chunker NewChunker(ISentenceSplitter? splitter = null, ITokenizer? tokenizer = null) =>
        new(tokenizer ?? new WordCountTokenizer(), splitter ?? Punctuation);

    /// <summary>Sentences of exactly ten words, numbered so every sentence is unique.</summary>
    private static string Prose(string tag, int sentences, int first = 0) =>
        string.Join(" ", Enumerable.Range(first, sentences).Select(i => $"The {tag} sentence number {i} has ten words in it."));

    private static ExtractedBook Book(IEnumerable<TextBlock> blocks, int sections = 1, string language = "en") =>
        new(new BookMetadata("Test", null), "txt", language,
            Enumerable.Range(0, sections).Select(i => new ExtractedSection(i, $"S{i}", null, 1)).ToList(),
            blocks.ToList());

    private static ExtractedBook OneSection(string text) => Book([new TextBlock(0, text)]);

    // ---- size, overlap, structure -------------------------------------------------------------------------

    [Fact]
    public void Chunk_RespectsTargetSize()
    {
        var chunks = NewChunker().Chunk(OneSection(Prose("alpha", 400)));

        Assert.True(chunks.Count >= 7);
        foreach (var c in chunks.SkipLast(1))
            Assert.InRange(c.TokenCount, 400, 550);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= 550));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
    }

    [Fact]
    public void Chunk_OverlapsNeighbors()
    {
        var chunks = NewChunker().Chunk(OneSection(Prose("alpha", 400)));

        for (var i = 0; i + 1 < chunks.Count; i++)
        {
            var shared = SharedWords(chunks[i].Text, chunks[i + 1].Text);
            Assert.InRange(shared, 60, 90);
        }
    }

    private static int SharedWords(string a, string b)
    {
        var x = a.Split(' ');
        var y = b.Split(' ');
        for (var k = Math.Min(x.Length, y.Length); k > 0; k--)
            if (x[^k..].SequenceEqual(y[..k])) return k;
        return 0;
    }

    [Fact]
    public void Chunk_NeverCrossesSection()
    {
        var book = Book([new TextBlock(0, Prose("alpha", 120)), new TextBlock(1, Prose("beta", 120))], sections: 2);

        var chunks = NewChunker().Chunk(book);

        Assert.All(chunks, c =>
            Assert.False(c.Text.Contains("alpha") && c.Text.Contains("beta"), "chunk mixes sections"));
        Assert.All(chunks.Where(c => c.Text.Contains("alpha")), c => Assert.Equal(0, c.SectionOrder));
        Assert.All(chunks.Where(c => c.Text.Contains("beta")), c => Assert.Equal(1, c.SectionOrder));
        // Overlap doesn't carry over: the first chunk of section 1 starts with its own first sentence.
        Assert.StartsWith("The beta sentence number 0", chunks.First(c => c.SectionOrder == 1).Text);
    }

    [Fact]
    public void Chunk_LongSentence_IsSplit()
    {
        var sentence = string.Join(" ", Enumerable.Range(0, 1200).Select(i => $"w{i}")) + ".";

        var chunks = NewChunker().Chunk(OneSection(sentence));

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= 500));
        Assert.Equal(1200, chunks.Sum(c => c.TokenCount));
        // Nothing is lost or reordered.
        Assert.Equal(sentence, string.Join(" ", chunks.Select(c => c.Text)));
    }

    [Fact]
    public void Chunk_LongSentenceWithoutSpaces_IsSplitByCharacter()
    {
        var text = new string('x', 1300) + ".";

        var chunks = NewChunker(tokenizer: new CharTokenizer()).Chunk(OneSection(text));

        Assert.Equal(3, chunks.Count);
        Assert.Equal(text, string.Concat(chunks.Select(c => c.Text)));
    }

    private sealed class CharTokenizer : ITokenizer
    {
        public IReadOnlyList<int> Count(IReadOnlyList<string> texts) => texts.Select(t => t.Length).ToList();
    }

    [Fact]
    public void Chunk_IsDeterministic()
    {
        var book = OneSection(Prose("alpha", 300));

        var first = NewChunker().Chunk(book);
        var second = NewChunker().Chunk(book);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Chunk_PageRange_SpansCorrectly()
    {
        // 45 sentences on page 4 then 15 on page 5: the first chunk (50 sentences) crosses the page break, and the
        // overlap carries the last two page-4 sentences into the second chunk, so it spans both pages too.
        var book = Book([new TextBlock(0, Prose("p4", 45), Page: 4), new TextBlock(0, Prose("p5", 15), Page: 5)]);

        var chunks = NewChunker().Chunk(book);

        Assert.Equal(2, chunks.Count);
        Assert.Equal((4, 5), (chunks[0].PageStart, chunks[0].PageEnd));
        Assert.Equal((4, 5), (chunks[1].PageStart, chunks[1].PageEnd));
    }

    [Fact]
    public void Chunk_SinglePageChunk_HasSameStartAndEnd()
    {
        var chunk = Assert.Single(NewChunker().Chunk(Book([new TextBlock(0, Prose("a", 5), Page: 9)])));

        Assert.Equal((9, 9), (chunk.PageStart, chunk.PageEnd));
    }

    [Fact]
    public void Chunk_Paragraphs_AreKeptSeparatedInText()
    {
        var book = Book([new TextBlock(0, "First paragraph here."), new TextBlock(0, "Second paragraph here.")]);

        var chunk = Assert.Single(NewChunker().Chunk(book));

        Assert.Equal("First paragraph here.\n\nSecond paragraph here.", chunk.Text);
    }

    [Fact]
    public void Chunk_Location_PointsAtFirstSentenceOffset()
    {
        var text = "Alpha one. Beta two. Gamma three.";
        var book = Book([new TextBlock(0, text, Location: "ch1.xhtml#100")]);

        var chunk = Assert.Single(NewChunker().Chunk(book));

        Assert.Equal("ch1.xhtml#100", chunk.Location);
        Assert.Equal("ch1.xhtml#111", Chunker.Shift("ch1.xhtml#100", text.IndexOf("Beta", StringComparison.Ordinal)));
        Assert.Null(Chunker.Shift(null, 5));
        Assert.Equal("nohash", Chunker.Shift("nohash", 5));
    }

    [Fact]
    public void Chunk_Locations_MapBackToChunkStartInSourceText()
    {
        var text = Prose("alpha", 200);
        var book = Book([new TextBlock(0, text, Location: "ch1.xhtml#1000")]);

        var chunks = NewChunker().Chunk(book);

        Assert.True(chunks.Count > 2);
        foreach (var c in chunks)
        {
            var offset = int.Parse(c.Location!.Split('#')[1]) - 1000;
            Assert.StartsWith(c.Text[..30], text[offset..]);
        }
    }

    [Fact]
    public void Chunk_EmptyBook_ReturnsNothing() =>
        Assert.Empty(NewChunker().Chunk(Book([])));

    [Fact]
    public void Chunk_SectionsWithoutText_AreSkipped()
    {
        var book = Book([new TextBlock(2, "Only the last section has text.")], sections: 3);

        var chunk = Assert.Single(NewChunker().Chunk(book));

        Assert.Equal(2, chunk.SectionOrder);
        Assert.Equal(0, chunk.Index);
    }

    [Fact]
    public void Chunk_ShortSection_BecomesOneChunkWithoutOverlap()
    {
        var chunk = Assert.Single(NewChunker().Chunk(OneSection(Prose("a", 3))));

        Assert.Equal(30, chunk.TokenCount);
    }

    // ---- sentence splitting -------------------------------------------------------------------------------

    private static IEnumerable<ISentenceSplitter> Splitters()
    {
        yield return Punctuation;
        if (IcuSentenceSplitter.TryCreate() is { } icu) yield return icu;
    }

    private static List<string> Sentences(ISentenceSplitter splitter, string text, string? language = null) =>
        splitter.Split(text, language).Select(s => text.Substring(s.Start, s.Length)).ToList();

    [Fact]
    public void Split_Japanese_UsesFullWidthStops()
    {
        const string text = "私はその人を常に先生と呼んでいた。だからここではただ先生と書く。本名は打ち明けない！";
        foreach (var splitter in Splitters())
            Assert.Equal(
                ["私はその人を常に先生と呼んでいた。", "だからここではただ先生と書く。", "本名は打ち明けない！"],
                Sentences(splitter, text, "ja"));
    }

    [Fact]
    public void Split_Arabic_UsesArabicQuestionMark()
    {
        const string text = "هل وجدت الكتاب؟ نعم وجدته في السوق.";
        foreach (var splitter in Splitters())
            Assert.Equal(["هل وجدت الكتاب؟", "نعم وجدته في السوق."], Sentences(splitter, text, "ar"));
    }

    [Fact]
    public void Split_Abbreviations_NotSentenceEnds()
    {
        const string text = "Dr. Smith met Mr. Jones at 3.5 percent. They talked about J. K. Rowling, etc. and more. Then they left.";
        foreach (var splitter in Splitters())
            Assert.Equal(
                ["Dr. Smith met Mr. Jones at 3.5 percent.", "They talked about J. K. Rowling, etc. and more.", "Then they left."],
                Sentences(splitter, text, "en"));
    }

    [Fact]
    public void Split_QuotesAndEllipsis_StayWithTheirSentence()
    {
        const string text = "\"Where are you going?\" she asked. He did not answer... Then he left!";
        foreach (var splitter in Splitters())
        {
            var sentences = Sentences(splitter, text, "en");

            Assert.Equal("\"Where are you going?\" she asked.", sentences[0]);
            Assert.Equal(3, sentences.Count);
        }

        // A quoted question followed by a new capitalised sentence is still two sentences.
        foreach (var splitter in Splitters())
            Assert.Equal(["\"Really?\"", "She nodded."], Sentences(splitter, "\"Really?\" She nodded.", "en"));
    }

    [Fact]
    public void Split_SpansAreTrimmedAndCoverEverything()
    {
        const string text = "  One.   Two!  \n\n Three?  ";
        foreach (var splitter in Splitters())
            Assert.Equal(["One.", "Two!", "Three?"], Sentences(splitter, text));
    }

    [SkippableFact]
    public void Icu_IsAvailableOnThisPlatform() =>
        Skip.IfNot(IcuSentenceSplitter.IsAvailable, "ICU not found; set LD_LIBRARY_PATH to a libicu folder to run the ICU tests");

    // ---- tokenizer ----------------------------------------------------------------------------------------

    [Fact]
    public void ApproximateTokenizer_CountsCjkPerCharacterAndLatinPerFourCharacters()
    {
        var counts = new ApproximateTokenizer().Count(["日本語です", "abcdefgh", ""]);

        Assert.Equal([5, 2, 1], counts);
    }
}
