namespace BookSearch.Parsing;

/// <summary>Entry point: picks the extractor by file type and applies the size, DRM and image-only rules.</summary>
public static class Extractor
{
    public const long MaxFileBytes = 500L * 1024 * 1024;
    public const int MaxPdfPages = 5000;

    public static ExtractionResult Extract(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".epub" or ".pdf" or ".txt" or ".md"))
            return ExtractionResult.Reject(RejectionCode.Unsupported,
                $"Unsupported file type '{ext}'. Supported: EPUB, PDF, TXT, MD.");

        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Book file not found.", path);
        if (info.Length > MaxFileBytes)
            return ExtractionResult.Reject(RejectionCode.TooLarge,
                $"File is {info.Length / (1024 * 1024)} MB; the limit is {MaxFileBytes / (1024 * 1024)} MB.");

        try
        {
            var book = ext switch
            {
                ".epub" => EpubExtractor.Extract(path),
                ".pdf" => PdfExtractor.Extract(path),
                ".md" => TextExtractors.ExtractMarkdown(path),
                _ => TextExtractors.ExtractText(path),
            };
            return ExtractionResult.Ok(book);
        }
        catch (RejectedException e)
        {
            return ExtractionResult.Reject(e.Code, e.Message);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or FormatException
                                      or InvalidOperationException or ArgumentException
                                      or UglyToad.PdfPig.Core.PdfDocumentFormatException)
        {
            return ExtractionResult.Reject(RejectionCode.Corrupt, $"The file could not be read: {e.Message}");
        }
    }

    internal static string NormalizeWhitespace(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

    internal static string TitleFromFileName(string path) => Path.GetFileNameWithoutExtension(path);
}
