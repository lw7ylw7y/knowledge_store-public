using System.Text;
using System.Text.RegularExpressions;

namespace BookSearch.Parsing;

/// <summary>TXT and Markdown extraction, with BOM and fallback encoding detection.</summary>
public static partial class TextExtractors
{
    public static ExtractedBook ExtractText(string path)
    {
        var text = ReadText(path);
        var title = Extractor.TitleFromFileName(path);
        var sections = new List<ExtractedSection> { new(0, title, null, 1) };
        return new ExtractedBook(new BookMetadata(title, null), "txt", LanguageDetector.Detect(text),
            sections, Paragraphs(text, 0, 0), null);
    }

    public static ExtractedBook ExtractMarkdown(string path)
    {
        var text = ReadText(path);
        string? title = null, author = null;
        var bodyStart = 0;
        if (FrontMatter().Match(text) is { Success: true } fm)
        {
            foreach (Match kv in FrontMatterKey().Matches(fm.Groups[1].Value))
            {
                var value = kv.Groups[2].Value.Trim().Trim('"', '\'');
                if (kv.Groups[1].Value.Equals("title", StringComparison.OrdinalIgnoreCase)) title = value;
                else if (kv.Groups[1].Value.Equals("author", StringComparison.OrdinalIgnoreCase)) author = value;
            }
            bodyStart = fm.Length;
        }

        var sections = new List<ExtractedSection>();
        var blocks = new List<TextBlock>();
        var stack = new Stack<ExtractedSection>(); // open headings, outermost at the bottom
        var currentOrder = -1;
        var inFence = false;
        var paragraph = new StringBuilder();
        var paragraphStart = 0;

        void Flush()
        {
            if (paragraph.Length == 0) return;
            if (currentOrder < 0) // text before the first heading
            {
                var intro = new ExtractedSection(sections.Count, null, null, 0);
                sections.Add(intro);
                currentOrder = intro.Order;
            }
            var raw = paragraph.ToString();
            var leading = raw.Length - raw.TrimStart().Length;
            blocks.Add(new TextBlock(currentOrder, raw.Trim(), null, $"#{paragraphStart + leading}"));
            paragraph.Clear();
        }

        var offset = bodyStart;
        foreach (var rawLine in text[bodyStart..].Split('\n'))
        {
            var lineStart = offset;
            offset += rawLine.Length + 1;
            var line = rawLine.TrimEnd('\r');

            if (line.TrimStart().StartsWith("```")) inFence = !inFence;
            var heading = inFence ? null : Heading().Match(line);
            if (heading is { Success: true })
            {
                Flush();
                var level = heading.Groups[1].Length;
                var headingTitle = heading.Groups[2].Value.Trim();
                while (stack.Count > 0 && stack.Peek().Level >= level) stack.Pop();
                var section = new ExtractedSection(sections.Count, headingTitle, stack.Count > 0 ? stack.Peek().Order : null, level);
                sections.Add(section);
                stack.Push(section);
                currentOrder = section.Order;
                title ??= level == 1 ? headingTitle : null;
            }
            else if (!inFence && line.Trim().Length == 0) Flush();
            else
            {
                if (paragraph.Length == 0) paragraphStart = lineStart;
                else paragraph.Append('\n');
                paragraph.Append(line);
            }
        }
        Flush();

        if (sections.Count == 0) sections.Add(new ExtractedSection(0, Extractor.TitleFromFileName(path), null, 1));
        title ??= Extractor.TitleFromFileName(path);
        return new ExtractedBook(new BookMetadata(title, author), "md", LanguageDetector.Detect(text),
            sections, blocks, null);
    }

    internal static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Decode(bytes);
    }

    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    private static List<TextBlock> Paragraphs(string text, int sectionOrder, int baseOffset)
    {
        var blocks = new List<TextBlock>();
        foreach (Match m in ParagraphRegex().Matches(text))
        {
            var p = m.Value.Trim();
            if (p.Length == 0) continue;
            var leading = m.Value.Length - m.Value.TrimStart().Length; // offset points at the trimmed text
            blocks.Add(new TextBlock(sectionOrder, p, null, $"#{baseOffset + m.Index + leading}"));
        }
        return blocks;
    }

    [GeneratedRegex(@"\A---\r?\n(.*?)\r?\n---\r?\n", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();

    [GeneratedRegex(@"^(\w+):\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex FrontMatterKey();

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"[^\r\n](?:[^\r\n]|\r?\n(?![ \t]*\r?\n))*")]
    private static partial Regex ParagraphRegex();
}
