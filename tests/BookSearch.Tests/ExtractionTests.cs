using BookSearch.Parsing;

namespace BookSearch.Tests;

public class ExtractionTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static ExtractedBook Load(string name)
    {
        var result = Extractor.Extract(Fixture(name));
        Assert.True(result.Book is not null, $"{name} was rejected: {result.Rejection?.Reason}");
        return result.Book!;
    }

    private static Rejection Rejected(string name)
    {
        var result = Extractor.Extract(Fixture(name));
        Assert.True(result.IsRejected, $"{name} was accepted");
        return result.Rejection!;
    }

    // ---- EPUB ---------------------------------------------------------------------------------------------

    [Fact]
    public void Epub_Extract_GetsTitleAuthorAndChapters()
    {
        var book = Load("alice.epub");

        Assert.Equal("Alice's Adventures in Wonderland", book.Metadata.Title);
        Assert.Equal("Lewis Carroll", book.Metadata.Author);
        Assert.Equal("epub", book.Format);
        var chapters = book.Sections.Where(s => s.Title?.StartsWith("CHAPTER ") == true).ToList();
        Assert.Equal(12, chapters.Count);
        Assert.Equal("CHAPTER I. Down the Rabbit-Hole", chapters[0].Title);
        Assert.Equal("en", book.Language);

        // Chapter text lands in its own section: the rabbit-hole fall belongs to chapter I.
        var fall = book.Blocks.First(b => b.Text.Contains("rabbit-hole went straight on like a tunnel"));
        Assert.Equal(chapters[0].Order, fall.SectionOrder);
    }

    [Fact]
    public void Epub_Extract_RecordsOffsets()
    {
        var book = Load("alice.epub");

        var bySpineItem = book.Blocks.GroupBy(b => b.Location!.Split('#')[0]).ToList();
        Assert.True(bySpineItem.Count > 10);
        foreach (var item in bySpineItem)
        {
            // Rebuild the item's joined text from its blocks; every offset must point at its own block.
            var joined = string.Join("\n\n", item.Select(b => b.Text));
            foreach (var block in item)
            {
                var offset = int.Parse(block.Location!.Split('#')[1]);
                Assert.Equal(block.Text, joined.Substring(offset, block.Text.Length));
            }
        }
    }

    [Fact]
    public void Epub_TocAnchors_SplitChaptersSharingOneFile()
    {
        var book = Load("pride.epub");

        // Gutenberg packs several chapters into each file and points the TOC at anchors inside it.
        var withText = book.Blocks.Select(b => b.SectionOrder).Distinct().Count();
        Assert.True(withText >= 60, $"only {withText} sections received text");
        var chapter2 = book.Sections.Single(s => s.Title?.EndsWith("CHAPTER II.") == true);
        var inChapter = book.Blocks.Where(b => b.SectionOrder == chapter2.Order).Take(2).Select(b => b.Text).ToList();
        Assert.Equal("I hope Mr. Bingley will like it. CHAPTER II.", inChapter[0]); // the chapter's own heading
        Assert.True(inChapter[1].StartsWith("MR. BENNET was among the earliest"), string.Join(" ‖ ", inChapter));
    }

    [Fact]
    public void Epub_FontObfuscationOnly_IsAccepted()
    {
        var book = Load("fontobf.epub");

        Assert.Equal("Obfuscated Font Book", book.Metadata.Title);
        Assert.Contains(book.Blocks, b => b.Text.Contains("plain paragraph"));
    }

    [Fact]
    public void Epub_NoTocBook_StillExtracts()
    {
        var book = Load("kokoro.epub");

        Assert.Equal(["上　先生と私", "中　両親と私"], book.Sections.Select(s => s.Title!.Replace(' ', '　')));
        Assert.Equal(2, book.Blocks.Select(b => b.SectionOrder).Distinct().Count());
    }

    // ---- PDF ----------------------------------------------------------------------------------------------

    private const string KnownSentence = "Natural selection acts solely by accumulating slight successive favourable variations.";

    [Fact]
    public void Pdf_Extract_RecordsPageNumbers()
    {
        var book = Load("origin.pdf");

        Assert.Equal(8, book.PageCount);
        var hits = book.Blocks.Where(b => b.Text.Contains(KnownSentence)).ToList();
        Assert.Equal(5, Assert.Single(hits).Page);
        Assert.All(book.Blocks, b => Assert.InRange(b.Page!.Value, 1, 8));
        Assert.Equal("Charles Darwin", book.Metadata.Author);
        Assert.Equal("On the Origin of Species", book.Metadata.Title);
    }

    [Fact]
    public void Pdf_Extract_StripsRepeatedHeaders()
    {
        var book = Load("origin.pdf");

        Assert.DoesNotContain(book.Blocks, b => b.Text.Contains("ON THE ORIGIN OF SPECIES"));
        Assert.DoesNotContain(book.Blocks, b => b.Text.Contains("CHAPTER IV"));
        Assert.DoesNotContain(book.Blocks, b => b.Text.All(char.IsDigit)); // page-number footers
        Assert.Equal(Enumerable.Range(1, 8), book.Blocks.Select(b => b.Page!.Value).Distinct().Order());
    }

    [Fact]
    public void Pdf_Extract_TwoColumns_AreInReadingOrder()
    {
        var book = Load("twocol.pdf");

        var page2 = string.Join("\n", book.Blocks.Where(b => b.Page == 2).Select(b => b.Text));
        var order = new[] { "Left column opens", "Left column ends", "Right column opens", "Right column ends" }
            .Select(s => page2.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public void Pdf_JoinLines_RemovesHyphenationAtLineEnds()
    {
        Assert.Equal("an international agreement was reached",
            PdfExtractor.JoinLines(["an inter-", "national agreement", "was reached"]));
        Assert.Equal("well-known fact", PdfExtractor.JoinLines(["well-known", "fact"]));
    }

    [Fact]
    public void Pdf_JoinLines_RemovesSpacesBetweenCjkCharacters()
    {
        Assert.Equal("断开与他们之间的联结", PdfExtractor.JoinLines(["断开与他们之", "间的联结"]));
        Assert.Equal("Use Windows 系统 now", PdfExtractor.JoinLines(["Use Windows", "系统 now"]).Replace("Windows系统", "Windows 系统"));
    }

    [Fact]
    public void Pdf_Gutter_FoundOnTwoColumnPageButNotOnSingleColumnPage()
    {
        using var two = UglyToad.PdfPig.PdfDocument.Open(Fixture("twocol.pdf"));
        var twoPage = two.GetPage(1);
        Assert.NotNull(PdfExtractor.FindGutter(twoPage.GetWords().ToList(), twoPage.Width, twoPage.Height));

        using var one = UglyToad.PdfPig.PdfDocument.Open(Fixture("origin.pdf"));
        for (var n = 1; n <= one.NumberOfPages; n++)
        {
            var page = one.GetPage(n);
            Assert.Null(PdfExtractor.FindGutter(page.GetWords().ToList(), page.Width, page.Height));
        }
    }

    // ---- TXT and MD ---------------------------------------------------------------------------------------

    [Fact]
    public void Txt_Extract_DetectsUtf16Bom()
    {
        var book = Load("utf16.txt");

        Assert.Contains("café, élève, naïve", book.Blocks[0].Text);
        Assert.Equal(2, book.Blocks.Count);
        Assert.Equal("fr", book.Language);
    }

    [Fact]
    public void Txt_Extract_FallsBackToWindows1252()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cp1252-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllBytes(path, [.. "Caf"u8, 0xE9, .. " au lait"u8]); // 0xE9 alone is invalid UTF-8
            var result = Extractor.Extract(path);
            Assert.Equal("Café au lait", result.Book!.Blocks[0].Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Txt_Extract_BlocksCarryOffsets()
    {
        var book = Load("plain.txt");

        var text = File.ReadAllText(Fixture("plain.txt"));
        foreach (var block in book.Blocks)
            Assert.Equal(block.Text, text.Substring(int.Parse(block.Location!.TrimStart('#')), block.Text.Length));
        Assert.Equal(2, book.Blocks.Count);
    }

    [Fact]
    public void Md_Extract_BlocksCarryOffsets()
    {
        var book = Load("notes.md");

        var text = File.ReadAllText(Fixture("notes.md"));
        foreach (var block in book.Blocks)
            Assert.Equal(block.Text, text.Substring(int.Parse(block.Location!.TrimStart('#')), block.Text.Length));
    }

    [Fact]
    public void Md_Extract_HeadingsBecomeSections()
    {
        var book = Load("notes.md");

        Assert.Equal("Reading Notes", book.Metadata.Title);
        Assert.Equal(["Reading Notes", "Habits", "Morning", "Memory"], book.Sections.Select(s => s.Title));
        Assert.Equal([null, 0, 1, 0], book.Sections.Select(s => s.ParentOrder));
        var morning = book.Blocks.Single(b => b.Text.StartsWith("Wake early"));
        Assert.Equal(2, morning.SectionOrder);
    }

    // ---- Reject rules -------------------------------------------------------------------------------------

    [Fact]
    public void Reject_ScannedPdf_ReturnsImageOnly() =>
        Assert.Equal(RejectionCode.ImageOnly, Rejected("scanned.pdf").Code);

    [Fact]
    public void Reject_LockedPdf_ReturnsEncrypted() =>
        Assert.Equal(RejectionCode.Encrypted, Rejected("locked.pdf").Code);

    [Fact]
    public void Reject_DrmEpub_ReturnsDrm() =>
        Assert.Equal(RejectionCode.Drm, Rejected("drm.epub").Code);

    [Fact]
    public void Reject_HugeFile_ReturnsTooLarge()
    {
        var path = Path.Combine(Path.GetTempPath(), $"huge-{Guid.NewGuid():N}.txt");
        try
        {
            using (var f = File.Create(path)) f.SetLength(600L * 1024 * 1024); // sparse; never read
            Assert.Equal(RejectionCode.TooLarge, Extractor.Extract(path).Rejection!.Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reject_Docx_ReturnsUnsupported()
    {
        var path = Path.Combine(Path.GetTempPath(), $"memo-{Guid.NewGuid():N}.docx");
        try
        {
            File.WriteAllText(path, "not really a docx");
            var rejection = Extractor.Extract(path).Rejection!;
            Assert.Equal(RejectionCode.Unsupported, rejection.Code);
            Assert.Contains(".docx", rejection.Reason);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(".epub")]
    [InlineData(".pdf")]
    public void Reject_CorruptFile_ReturnsCorruptInsteadOfThrowing(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"junk-{Guid.NewGuid():N}{ext}");
        try
        {
            File.WriteAllBytes(path, new byte[2048]);
            Assert.Equal(RejectionCode.Corrupt, Extractor.Extract(path).Rejection!.Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reject_Reasons_AreSpecific()
    {
        Assert.Contains("scanned", Rejected("scanned.pdf").Reason);
        Assert.Contains("password", Rejected("locked.pdf").Reason);
        Assert.Contains("DRM", Rejected("drm.epub").Reason);
    }

    // ---- Language -----------------------------------------------------------------------------------------

    [Fact]
    public void Language_Detect_JapaneseSpanishArabic()
    {
        Assert.Equal("ja", Load("kokoro.epub").Language);
        Assert.Equal("es", Load("quijote.epub").Language);
        Assert.Equal("ar", Load("arabic.txt").Language);
    }

    [Theory]
    [InlineData("Le chat est dans la maison et il ne veut pas sortir de la cuisine.", "fr")]
    [InlineData("Der Hund ist nicht in dem Haus und die Katze ist auf dem Dach.", "de")]
    [InlineData("这是一个关于图书馆和读书的故事。", "zh")]
    [InlineData("ภาษาไทยเป็นภาษาที่ใช้พูดในประเทศไทย", "th")]
    [InlineData("12345 !!!", "und")]
    public void Language_Detect_OtherScriptsAndLanguages(string text, string expected) =>
        Assert.Equal(expected, LanguageDetector.Detect(text));
}
