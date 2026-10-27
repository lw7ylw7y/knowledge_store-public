using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using VersOne.Epub;

namespace BookSearch.Parsing;

public static class EpubExtractor
{
    // Algorithms that only scramble embedded fonts; the book text itself stays readable.
    private static readonly string[] FontObfuscation =
    [
        "http://www.idpf.org/2008/embedding",
        "http://ns.adobe.com/pdf/enc#RC",
    ];

    private const string BlockSelector = "p,h1,h2,h3,h4,h5,h6,li,pre,dt,dd,blockquote,div,td,th,figcaption";

    public static ExtractedBook Extract(string path)
    {
        CheckForDrm(path);

        var epub = EpubReader.ReadBook(path);
        var title = Extractor.NormalizeWhitespace(epub.Title ?? "");
        if (title.Length == 0) title = Extractor.TitleFromFileName(path);
        var author = epub.AuthorList?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
        var authorText = author is { Count: > 0 } ? string.Join("; ", author) : null;

        // Spine items in reading order -> their blocks (offsets into each item's joined text) and anchor targets.
        var spine = epub.ReadingOrder
            .Select(f => (Path: f.FilePath, Parsed: ParseBlocks(f.Content)))
            .ToList();

        var sections = new List<ExtractedSection>();
        var entries = new List<(int Section, string File, string? Anchor)>();
        AddNavigation(epub.Navigation, null, 1, sections, entries);

        if (sections.Count == 0) // no usable TOC: one section per spine item
            for (var i = 0; i < spine.Count; i++)
            {
                var first = spine[i].Parsed.Blocks.FirstOrDefault().Text;
                sections.Add(new ExtractedSection(sections.Count, first is { Length: <= 120 } ? first : $"Part {i + 1}", null, 1));
                entries.Add((sections.Count - 1, spine[i].Path, null));
            }

        // Each TOC entry starts at its anchor's block (or the file's first block). A block belongs to the latest
        // entry that starts at or before it; text before the first entry is left unassigned (-1) for now.
        var assigned = new List<(string Path, string Text, int Offset, int Section)>();
        var current = -1;
        foreach (var (itemPath, parsed) in spine)
        {
            var starts = entries
                .Where(e => string.Equals(e.File, itemPath, StringComparison.OrdinalIgnoreCase))
                .Select(e => (Block: e.Anchor is not null && parsed.Anchors.TryGetValue(e.Anchor, out var idx) ? idx : 0, e.Section))
                .ToList();
            for (var i = 0; i < parsed.Blocks.Count; i++)
            {
                foreach (var start in starts.Where(st => st.Block == i)) current = start.Section; // later entry wins
                assigned.Add((itemPath, parsed.Blocks[i].Text, parsed.Blocks[i].Offset, current));
            }
            if (parsed.Blocks.Count == 0 && starts.Count > 0) current = starts[^1].Section;
        }

        // Front matter before the first TOC entry goes into a leading untitled section.
        var lead = assigned.Any(a => a.Section < 0);
        if (lead)
        {
            for (var i = 0; i < sections.Count; i++)
                sections[i] = sections[i] with { Order = i + 1, ParentOrder = sections[i].ParentOrder + 1 };
            sections.Insert(0, new ExtractedSection(0, null, null, 0));
        }
        var blocks = assigned
            .Select(a => new TextBlock(lead ? a.Section + 1 : a.Section, a.Text, null, $"{a.Path}#{a.Offset}"))
            .ToList();

        var language = LanguageDetector.Detect(string.Join(' ', blocks.Select(b => b.Text)));
        return new ExtractedBook(new BookMetadata(title, authorText), "epub", language, sections, blocks, null);
    }

    private static void AddNavigation(IEnumerable<EpubNavigationItem>? items, int? parent, int level,
        List<ExtractedSection> sections, List<(int Section, string File, string? Anchor)> entries)
    {
        foreach (var item in items ?? [])
        {
            var section = new ExtractedSection(sections.Count, Extractor.NormalizeWhitespace(item.Title ?? ""), parent, level);
            sections.Add(section);
            var file = item.Link?.ContentFilePath;
            if (!string.IsNullOrEmpty(file)) entries.Add((section.Order, file, item.Link?.Anchor));
            AddNavigation(item.NestedItems, section.Order, level + 1, sections, entries);
        }
    }

    /// <summary>
    /// Leaf block elements of an XHTML document as whitespace-normalized text with joined-text offsets, plus a map
    /// from element ids (TOC anchors) to the index of the block they point at.
    /// </summary>
    internal static (List<(string Text, int Offset)> Blocks, Dictionary<string, int> Anchors) ParseBlocks(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var blocks = new List<(string, int)>();
        var anchors = new Dictionary<string, int>();
        var pending = new List<string>();   // ids seen before the block they point at
        IElement? currentLeaf = null;
        var offset = 0;

        foreach (var el in doc.Body?.Descendants<IElement>() ?? [])
        {
            var id = el.Id ?? el.GetAttribute("name");
            if (currentLeaf is not null && currentLeaf.Contains(el))
            {
                if (!string.IsNullOrEmpty(id)) anchors.TryAdd(id, blocks.Count - 1); // anchor inside a block
                continue;
            }

            if (!string.IsNullOrEmpty(id)) pending.Add(id);
            if (!el.Matches(BlockSelector) || el.QuerySelector(BlockSelector) is not null) continue; // leaf blocks only
            var text = Extractor.NormalizeWhitespace(el.TextContent);
            if (text.Length == 0) continue;

            foreach (var p in pending) anchors.TryAdd(p, blocks.Count);
            pending.Clear();
            currentLeaf = el;
            blocks.Add((text, offset));
            offset += text.Length + 2; // blocks are joined by a blank line
        }
        return (blocks, anchors);
    }

    private static void CheckForDrm(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("META-INF/encryption.xml");
        if (entry is null) return;

        XDocument doc;
        using (var stream = entry.Open()) doc = XDocument.Load(stream);
        foreach (var data in doc.Descendants().Where(e => e.Name.LocalName == "EncryptedData"))
        {
            var algorithm = data.Descendants().FirstOrDefault(e => e.Name.LocalName == "EncryptionMethod")
                ?.Attribute("Algorithm")?.Value;
            if (algorithm is null || !FontObfuscation.Contains(algorithm))
                throw new RejectedException(RejectionCode.Drm,
                    "This EPUB is DRM-protected (its content is encrypted), so it can't be indexed.");
        }
    }
}
