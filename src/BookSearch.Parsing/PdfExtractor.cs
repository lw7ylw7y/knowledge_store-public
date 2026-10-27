using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Outline;
using PdfTextBlock = UglyToad.PdfPig.DocumentLayoutAnalysis.TextBlock;

namespace BookSearch.Parsing;

public static partial class PdfExtractor
{
    /// <summary>A page with fewer extracted characters than this has no usable text layer.</summary>
    public const int MinCharsPerTextPage = 100;
    /// <summary>The book is image-only when at least this share of pages has no usable text layer.</summary>
    public const double ImageOnlyPageShare = 0.8;

    private const double MarginShare = 0.12;      // top/bottom band searched for running headers and footers
    private const double RepeatShare = 0.4;       // a margin line repeating on this share of pages is boilerplate

    public static ExtractedBook Extract(string path)
    {
        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(path);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new RejectedException(RejectionCode.Encrypted,
                "This PDF is password-protected, so it can't be indexed.");
        }

        using (doc)
        {
            if (doc.NumberOfPages == 0)
                throw new RejectedException(RejectionCode.Corrupt, "The PDF has no pages.");
            if (doc.NumberOfPages > Extractor.MaxPdfPages)
                throw new RejectedException(RejectionCode.TooLarge,
                    $"The PDF has {doc.NumberOfPages} pages; the limit is {Extractor.MaxPdfPages}.");

            var pages = doc.GetPages().Select(p => (Page: p, Words: p.GetWords().ToList())).ToList();

            var sparse = pages.Count(p => p.Words.Sum(w => w.Text.Count(c => !char.IsWhiteSpace(c))) < MinCharsPerTextPage);
            if (sparse >= Math.Ceiling(pages.Count * ImageOnlyPageShare))
                throw new RejectedException(RejectionCode.ImageOnly,
                    "This PDF looks scanned (images without a text layer). OCR isn't supported.");

            var boilerplate = FindRepeatedMarginLines(pages.Select(p => (p.Page.Height, Lines(p.Words))).ToList());

            var title = doc.Information.Title is { Length: > 0 } t ? Extractor.NormalizeWhitespace(t) : Extractor.TitleFromFileName(path);
            var author = doc.Information.Author is { Length: > 0 } a ? Extractor.NormalizeWhitespace(a) : null;

            var (sections, startPages) = ReadSections(doc);
            var blocks = new List<TextBlock>();
            var allText = new StringBuilder();

            foreach (var (page, words) in pages)
            {
                var kept = words.Where(w => !boilerplate.Contains(w)).ToList();
                if (kept.Count == 0) continue;

                foreach (var block in ReadingOrderBlocks(kept, page.Width, page.Height))
                {
                    var text = JoinLines(block.TextLines.Select(l => l.Text));
                    if (text.Length == 0) continue;
                    var sectionOrder = SectionForPage(startPages, page.Number);
                    blocks.Add(new TextBlock(sectionOrder, text, page.Number));
                    allText.Append(text).Append(' ');
                }
            }

            return new ExtractedBook(new BookMetadata(title, author), "pdf",
                LanguageDetector.Detect(allText.ToString()), sections, blocks, doc.NumberOfPages);
        }
    }

    /// <summary>
    /// Non-space characters of extracted text on each page, to calibrate the image-only rule
    /// (fewer than <see cref="MinCharsPerTextPage"/> on <see cref="ImageOnlyPageShare"/> of pages).
    /// </summary>
    public static int[] CharsPerPage(string path)
    {
        using var doc = PdfDocument.Open(path);
        return doc.GetPages().Select(p => p.GetWords().Sum(w => w.Text.Count(c => !char.IsWhiteSpace(c)))).ToArray();
    }

    // ---- running header / footer detection -------------------------------------------------------------

    private sealed record Line(double Bottom, string Key, List<Word> Words);

    private static List<Line> Lines(List<Word> words)
    {
        var lines = new List<Line>();
        var current = new List<Word>();
        double bottom = 0;
        foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            if (current.Count > 0 && Math.Abs(w.BoundingBox.Bottom - bottom) > 3)
            {
                lines.Add(MakeLine(current));
                current = [];
            }
            if (current.Count == 0) bottom = w.BoundingBox.Bottom;
            current.Add(w);
        }
        if (current.Count > 0) lines.Add(MakeLine(current));
        return lines;
    }

    private static Line MakeLine(List<Word> words)
    {
        var ordered = words.OrderBy(w => w.BoundingBox.Left).ToList();
        var text = string.Join(' ', ordered.Select(w => w.Text));
        var key = System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"\d+", "#");
        return new Line(ordered[0].BoundingBox.Bottom, Extractor.NormalizeWhitespace(key), ordered);
    }

    private static HashSet<Word> FindRepeatedMarginLines(List<(double Height, List<Line> Lines)> pages)
    {
        var result = new HashSet<Word>();
        if (pages.Count < 3) return result;

        bool InMargin(double height, Line l) => l.Bottom > height * (1 - MarginShare) || l.Bottom < height * MarginShare;

        var pagesWithKey = new Dictionary<string, int>();
        foreach (var (height, lines) in pages)
            foreach (var key in lines.Where(l => InMargin(height, l)).Select(l => l.Key).Distinct())
                pagesWithKey[key] = pagesWithKey.GetValueOrDefault(key) + 1;

        var threshold = Math.Max(2, (int)Math.Ceiling(pages.Count * RepeatShare));
        foreach (var (height, lines) in pages)
            foreach (var line in lines.Where(l => InMargin(height, l) && pagesWithKey[l.Key] >= threshold))
                foreach (var w in line.Words) result.Add(w);
        return result;
    }

    // ---- layout -----------------------------------------------------------------------------------------

    /// <summary>
    /// Text blocks of a page in reading order. Two-column pages are read left column then right column; rows that
    /// cross the gutter (titles, running heads, footnotes) stay full-width and split the page into bands.
    /// </summary>
    internal static IEnumerable<PdfTextBlock> ReadingOrderBlocks(List<Word> words, double pageWidth, double pageHeight)
    {
        foreach (var unit in LayoutUnits(words, pageWidth, pageHeight))
        {
            var blocks = RecursiveXYCut.Instance.GetBlocks(unit);
            foreach (var block in UnsupervisedReadingOrderDetector.Instance.Get(blocks).Cast<PdfTextBlock>())
                yield return block;
        }
    }

    private static List<List<Word>> LayoutUnits(List<Word> words, double pageWidth, double pageHeight)
    {
        if (FindGutter(words, pageWidth, pageHeight) is not var (gapStart, gapEnd)) return [words];
        var middle = (gapStart + gapEnd) / 2;

        var units = new List<List<Word>>();
        List<Word> left = [], right = [];
        void FlushColumns()
        {
            if (left.Count > 0) units.Add(left);
            if (right.Count > 0) units.Add(right);
            left = [];
            right = [];
        }

        foreach (var line in Lines(words)) // top to bottom
        {
            // A word starting inside the gutter belongs to a full-width row (title, running head, footnote).
            if (line.Words.Any(w => w.BoundingBox.Left > gapStart && w.BoundingBox.Left < gapEnd))
            {
                FlushColumns();
                units.Add(line.Words);
                continue;
            }
            foreach (var w in line.Words)
                (w.BoundingBox.Left < middle ? left : right).Add(w);
        }
        FlushColumns();
        return units;
    }

    /// <summary>
    /// The x range of a column gutter, or null for a single-column page. A gutter is a run of at least 10 points near
    /// the middle where no body word starts, followed by a column edge: a word starting just after the gap on most
    /// text rows. Word start positions are used rather than right edges because some PDFs report inflated widths.
    /// </summary>
    internal static (double Start, double End)? FindGutter(List<Word> words, double pageWidth, double pageHeight)
    {
        // Running heads, page numbers and footnotes live in the margin bands and may span the gutter.
        var body = words.Where(w => w.BoundingBox.Bottom > pageHeight * MarginShare
                                    && w.BoundingBox.Bottom < pageHeight * (1 - MarginShare)).ToList();
        if (body.Count < 40) return null;

        var lo = (int)(pageWidth * 0.3);
        var starts = new int[(int)(pageWidth * 0.4) + 1];
        foreach (var w in body)
        {
            var bin = (int)w.BoundingBox.Left - lo;
            if (bin >= 0 && bin < starts.Length) starts[bin]++;
        }

        var best = (Start: 0, End: -1);
        int? runStart = null;
        for (var i = 0; i < starts.Length; i++)
        {
            if (starts[i] == 0)
            {
                runStart ??= i;
                if (i - runStart.Value > best.End - best.Start) best = (runStart.Value, i);
            }
            else runStart = null;
        }
        if (best.End - best.Start < 10) return null;

        double start = lo + best.Start, end = lo + best.End;
        var middle = (start + end) / 2;
        var leftCount = body.Count(w => w.BoundingBox.Left < middle);
        var minSide = body.Count * 0.25;
        if (leftCount < minSide || body.Count - leftCount < minSide) return null;

        // The right column must have a ragged-left edge right after the gap, on a good share of the text rows.
        var rows = Lines(body).Count;
        var edge = body.Count(w => w.BoundingBox.Left >= end && w.BoundingBox.Left <= end + 4);
        return edge >= Math.Max(6, rows * 0.4) ? (start, end) : null;
    }

    // ---- structure --------------------------------------------------------------------------------------

    /// <summary>
    /// Sections from the PDF outline, each with the page it starts on. Without an outline the whole book is one
    /// section. Pages before the first outline entry go into a leading untitled section.
    /// </summary>
    private static (List<ExtractedSection> Sections, List<int> StartPages) ReadSections(PdfDocument doc)
    {
        var sections = new List<ExtractedSection>();
        var starts = new List<int>();
        if (doc.TryGetBookmarks(out var bookmarks))
            Walk(bookmarks.Roots, null, 1);

        if (sections.Count == 0)
        {
            sections.Add(new ExtractedSection(0, null, null, 1));
            starts.Add(1);
        }
        else if (starts.Min() > 1)
        {
            for (var i = 0; i < sections.Count; i++)
                sections[i] = sections[i] with { Order = i + 1, ParentOrder = sections[i].ParentOrder + 1 };
            sections.Insert(0, new ExtractedSection(0, null, null, 0));
            starts.Insert(0, 1);
        }
        return (sections, starts);

        void Walk(IEnumerable<BookmarkNode> nodes, int? parent, int level)
        {
            foreach (var node in nodes)
            {
                var self = parent;
                if (node is DocumentBookmarkNode target)
                {
                    self = sections.Count;
                    sections.Add(new ExtractedSection(self.Value, Extractor.NormalizeWhitespace(target.Title), parent, level));
                    starts.Add(target.PageNumber);
                }
                Walk(node.Children, self, level + 1);
            }
        }
    }

    /// <summary>The last section starting on or before the page.</summary>
    private static int SectionForPage(List<int> startPages, int page)
    {
        var found = 0;
        for (var i = 0; i < startPages.Count; i++)
            if (startPages[i] <= page) found = i;
        return found;
    }

    /// <summary>Joins wrapped lines into one paragraph, removing hyphenation at line ends.</summary>
    internal static string JoinLines(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (sb.Length == 0) { sb.Append(line); continue; }
            if (sb[^1] == '-' && sb.Length > 1 && char.IsLetter(sb[^2]) && char.IsLower(line[0]))
                sb.Length--;           // "inter-" + "national" -> "international"
            else
                sb.Append(' ');
            sb.Append(line);
        }
        // Wrapped CJK text has no spaces between characters, so drop any a line break introduced.
        return CjkGap().Replace(Extractor.NormalizeWhitespace(sb.ToString()), "");
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?<=[\u3000-\u30FF\u3400-\u9FFF\uAC00-\uD7AF\uFF00-\uFFEF]) (?=[\u3000-\u30FF\u3400-\u9FFF\uAC00-\uD7AF\uFF00-\uFFEF])")]
    private static partial System.Text.RegularExpressions.Regex CjkGap();
}
