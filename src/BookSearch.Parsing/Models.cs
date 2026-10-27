namespace BookSearch.Parsing;

public sealed record BookMetadata(string Title, string? Author);

/// <summary>A chapter or section. <see cref="Order"/> is its position in reading order; blocks refer to it.</summary>
public sealed record ExtractedSection(int Order, string? Title, int? ParentOrder, int Level);

/// <summary>
/// One paragraph of text in reading order. PDF blocks carry a 1-based <see cref="Page"/>; EPUB blocks carry a
/// <see cref="Location"/> of the form "spine/item/path#offset", where the offset is the block's start in that
/// spine item's text (all of its blocks joined by a blank line); TXT and MD use "#offset" into the file text.
/// </summary>
public sealed record TextBlock(int SectionOrder, string Text, int? Page = null, string? Location = null);

public sealed record ExtractedBook(
    BookMetadata Metadata,
    string Format,
    string? Language,
    IReadOnlyList<ExtractedSection> Sections,
    IReadOnlyList<TextBlock> Blocks,
    int? PageCount = null);

public enum RejectionCode { Unsupported, TooLarge, Encrypted, Drm, ImageOnly, Corrupt }

public sealed record Rejection(RejectionCode Code, string Reason);

public sealed record ExtractionResult(ExtractedBook? Book, Rejection? Rejection)
{
    public bool IsRejected => Rejection is not null;
    public static ExtractionResult Ok(ExtractedBook book) => new(book, null);
    public static ExtractionResult Reject(RejectionCode code, string reason) => new(null, new Rejection(code, reason));
}

/// <summary>Thrown inside an extractor to reject the file with a specific reason.</summary>
public sealed class RejectedException(RejectionCode code, string reason) : Exception(reason)
{
    public RejectionCode Code { get; } = code;
}
