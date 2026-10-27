namespace BookSearch.Core.Search;

public sealed record SearchOptions
{
    /// <summary>How many results a search returns (and the summary is written from).</summary>
    public int TopN { get; init; } = 3;
    /// <summary>How many neighbours each retriever returns before fusion.</summary>
    public int CandidatesPerRetriever { get; init; } = 50;
    public int RrfK { get; init; } = 60;
    /// <summary>Best cosine similarity below this means the library doesn't cover the question (calibrated in ST-08).</summary>
    public double CoverageThreshold { get; init; } = 0.45;
    public int ExcerptChars { get; init; } = 320;

    /// <summary>
    /// A question word is highlighted only if it is in fewer than this share of the library's chunks (but see
    /// <see cref="CentralTermMinChunks"/>): everyday words such as "people" are not what the question is about.
    /// </summary>
    public double CentralTermShare { get; init; } = 0.02;

    /// <summary>The share rule never goes below this many chunks, so a very small library still highlights its words.</summary>
    public int CentralTermMinChunks { get; init; } = 5;

    /// <summary>
    /// Coverage threshold for a passage written in a different language from the question: across languages the same
    /// meaning scores lower, so the same-language threshold would call a correct answer "not covered". Not yet calibrated; set it equal to <see cref="CoverageThreshold"/> to disable.
    /// </summary>
    public double CrossLanguageCoverageThreshold { get; init; } = 0.35;

    /// <summary>The coverage rules from before cross-language handling (one threshold for every language), for comparing results.</summary>
    public static SearchOptions Classic => new()
    {
        CrossLanguageCoverageThreshold = double.MaxValue,   // never lower than the normal threshold
    };
}

public sealed record SearchHit(
    long ChunkId,
    long BookId,
    string? BookTitle,
    string? Author,
    string? SectionTitle,
    int? PageStart,
    int? PageEnd,
    string? Location,
    string Excerpt,
    IReadOnlyList<TextSpan> Highlights,
    string Text,
    double Score,
    double? Similarity,
    int? VectorRank,
    int? KeywordRank,
    int? TrigramRank,
    string? Language = null,
    int ChunkIndex = 0,
    long? SectionId = null);

/// <summary>Milliseconds spent in each step, for the console report and the latency budget.</summary>
public sealed record SearchTimings(double Keyword, double Embed, double Vector, double Fuse, double Load, double Total);

public sealed record SearchResult(
    IReadOnlyList<SearchHit> Hits,
    bool NotCovered,
    bool KeywordOnly,
    string? Warning,
    double? BestSimilarity,
    bool QueriedTrigramIndex,
    QueryTerms Terms,
    SearchTimings Timings,
    bool ExactKeywordMatch = false,
    bool FilterMatchedNoBooks = false,
    QueryTerms? CentralTerms = null)
{
    /// <summary>The question's words that are rare in the library: the only ones shown highlighted (all of them if not worked out).</summary>
    public QueryTerms HighlightTerms => CentralTerms ?? Terms;
}
