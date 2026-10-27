using System.Diagnostics;
using BookSearch.Core.Data;
using Microsoft.Data.Sqlite;

namespace BookSearch.Core.Search;

/// <summary>
/// Hybrid retrieval: word and trigram keyword search plus vector similarity, merged with reciprocal rank fusion.
/// Keyword search runs while the question is being embedded, and still returns results if embedding is unavailable.
/// </summary>
public sealed class SearchService(SqliteConnection conn, IQueryEmbedder? embedder, SearchOptions? options = null)
{
    private readonly SearchOptions _options = options ?? new SearchOptions();
    private const int CoverageCandidates = 10;

    private readonly ChunkRepository _chunks = new(conn);

    /// <summary>The coverage threshold for a passage: lower when its language differs from the question's (and both are known).</summary>
    private double ThresholdFor(string queryLanguage, string? passageLanguage) =>
        IsKnown(queryLanguage) && IsKnown(passageLanguage) && !string.Equals(queryLanguage, passageLanguage, StringComparison.OrdinalIgnoreCase)
            ? Math.Min(_options.CrossLanguageCoverageThreshold, _options.CoverageThreshold)
            : _options.CoverageThreshold;

    private static bool IsKnown(string? language) => !string.IsNullOrEmpty(language) && language != "und";

    /// <param name="bookIds">Search only these books (the user's tag or book filter, already resolved to indexed books); null searches the whole library. An empty list finds nothing and says so.</param>
    /// <param name="alsoSearch">The same question in other languages (DCR-11). Each one is searched exactly like the question (meaning and
    /// keywords), and all the lists are fused, so a passage in another language is found by a query written in its own language.
    /// With any translation every query is held to the normal coverage threshold (see below); without one the question's language is detected.</param>
    public async Task<SearchResult> SearchAsync(string question, CancellationToken ct = default, IReadOnlyCollection<long>? bookIds = null,
        IReadOnlyList<string>? alsoSearch = null)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Enter a question to search for.", nameof(question));
        var total = Stopwatch.StartNew();
        var queries = new List<string> { question };
        foreach (var extra in alsoSearch ?? [])
            if (!string.IsNullOrWhiteSpace(extra) && !queries.Any(q => string.Equals(q.Trim(), extra.Trim(), StringComparison.OrdinalIgnoreCase))) queries.Add(extra.Trim());
        var termsOf = queries.Select(q => QueryTerms.Extract(q)).ToList();
        var terms = QueryTerms.Union(termsOf);

        // A filter that matches no indexed book: nothing to search (an empty id list must never mean "everything").
        if (bookIds is { Count: 0 })
            return new SearchResult([], true, false, null, null, false, terms, new SearchTimings(0, 0, 0, 0, 0, total.Elapsed.TotalMilliseconds),
                FilterMatchedNoBooks: true);

        // Start embedding first: it is the slow step, and the keyword queries can run during it.
        var embedStarted = total.Elapsed;
        var embeddings = embedder is null ? null : queries.Select(q => Task.Run(() => embedder.EmbedQueryAsync(q, ct), ct)).ToList();

        var keywordStart = total.Elapsed;
        var wordIds = new List<IReadOnlyList<long>>();
        var trigramIds = new List<IReadOnlyList<long>>();
        foreach (var t in termsOf)
        {
            wordIds.Add(t.WordMatchExpression is { } w ? _chunks.FtsSearch(w, _options.CandidatesPerRetriever, bookIds: bookIds) : []);
            trigramIds.Add(t.TrigramMatchExpression is { } g
                ? _chunks.FtsSearch(g, _options.CandidatesPerRetriever, cjk: true, bookIds: bookIds)
                : []);
        }
        var keywordMs = (total.Elapsed - keywordStart).TotalMilliseconds;

        // Vector retrieval, if the embedding server answers.
        var vectorHits = queries.Select(_ => new List<VectorHit>()).ToList();
        string? warning = null;
        var keywordOnly = false;
        double embedMs = 0, vectorMs = 0;
        if (embeddings is null)
        {
            keywordOnly = true;
            warning = "No embedding server is configured; showing keyword matches only.";
        }
        else
        {
            try
            {
                var vectors = await Task.WhenAll(embeddings).ConfigureAwait(false);
                embedMs = (total.Elapsed - embedStarted).TotalMilliseconds;
                var knnStart = total.Elapsed;
                for (var i = 0; i < vectors.Length; i++)
                    vectorHits[i] = bookIds is null
                        ? _chunks.Knn(vectors[i], _options.CandidatesPerRetriever)
                        : _chunks.Knn(vectors[i], _options.CandidatesPerRetriever, bookIds);
                vectorMs = (total.Elapsed - knnStart).TotalMilliseconds;
            }
            catch (Exception e) when (e is ModelUnavailableException or InvalidDataException or TimeoutException or IOException)
            {
                keywordOnly = true;
                warning = "The embedding server isn't available; showing keyword matches only.";
                foreach (var list in vectorHits) list.Clear();
            }
        }

        // Fuse the meaning and keyword lists (of every query) and keep the best TopN, in fused order. (There used to be a slot for
        // the best meaning-only match, added last; it was removed in ST-27 because it showed the best meaning match after weaker ones.)
        // Three lists per query, the question's first: its ranks are the ones reported, the best rank over the queries for each kind.
        var fuseStart = total.Elapsed;
        var lists = new List<IReadOnlyList<long>>();
        for (var i = 0; i < queries.Count; i++)
        {
            lists.Add(vectorHits[i].Select(h => h.ChunkId).ToList());
            lists.Add(wordIds[i]);
            lists.Add(trigramIds[i]);
        }
        var fusedAll = ReciprocalRankFusion.Fuse(lists, int.MaxValue, _options.RrfK);
        var fused = fusedAll.Take(_options.TopN).ToList();
        var fuseMs = (total.Elapsed - fuseStart).TotalMilliseconds;

        // Load passages (the results, plus the best few vector matches so their languages are known) and build excerpts.
        var loadStart = total.Elapsed;
        var central = fused.Count > 0 ? CentralTermsOf(terms) : terms;      // nothing to highlight when nothing was found
        var coverageCandidates = vectorHits.Select(list => list.Take(CoverageCandidates).ToList()).ToList();
        var passages = _chunks.GetPassages(fused.Select(f => f.Id)
            .Concat(coverageCandidates.SelectMany(list => list.Select(h => h.ChunkId))).Distinct().ToList());
        var similarity = new Dictionary<long, double>();
        foreach (var h in vectorHits.SelectMany(list => list))
            similarity[h.ChunkId] = Math.Max(similarity.GetValueOrDefault(h.ChunkId, double.MinValue), 1 - h.Distance);
        int? BestRank(FusedItem f, int kind) =>
            Enumerable.Range(0, queries.Count).Select(q => f.Ranks[q * 3 + kind]).Where(r => r is not null).Min();
        var hits = fused.Where(f => passages.ContainsKey(f.Id)).Select(f =>
        {
            var p = passages[f.Id];
            var (excerpt, highlights) = ExcerptBuilder.Build(p.Text, terms, _options.ExcerptChars, central);
            return new SearchHit(p.ChunkId, p.BookId, p.BookTitle, p.Author, p.SectionTitle, p.PageStart, p.PageEnd,
                p.Location, excerpt, highlights, p.Text, f.Score,
                similarity.TryGetValue(f.Id, out var s) ? s : null, BestRank(f, 0), BestRank(f, 1), BestRank(f, 2), p.Language, p.ChunkIndex, p.SectionId);
        }).ToList();
        var loadMs = (total.Elapsed - loadStart).TotalMilliseconds;

        // Covered if, for any query, any of its best vector matches clears the threshold. With translations every language has a query
        // in its own language, so all use the normal threshold and the question's language is not needed. Without them a passage in
        // another language from the question gets the lower cross-language threshold (the language is detected).
        double? best = vectorHits.Any(list => list.Count > 0) ? vectorHits.Where(list => list.Count > 0).Max(list => 1 - list[0].Distance) : null;
        var covered = false;
        var translated = queries.Count > 1;
        for (var i = 0; i < queries.Count && !covered; i++)
        {
            var queryLanguage = translated ? null : BookSearch.Parsing.LanguageDetector.Detect(queries[i]);
            covered = coverageCandidates[i].Any(h =>
            {
                var language = passages.TryGetValue(h.ChunkId, out var p) ? p.Language : null;
                return 1 - h.Distance >= (queryLanguage is null ? _options.CoverageThreshold : ThresholdFor(queryLanguage, language));
            });
        }
        var exact = !keywordOnly && Enumerable.Range(0, queries.Count).Any(i => HasExactKeywordMatch(termsOf[i], wordIds[i]));
        var notCovered = keywordOnly ? hits.Count == 0 : !covered && !exact;

        total.Stop();
        return new SearchResult(hits, notCovered, keywordOnly, warning, best, terms.HasTrigrams, terms,
            new SearchTimings(keywordMs, embedMs, vectorMs, fuseMs, loadMs, total.Elapsed.TotalMilliseconds), exact,
            CentralTerms: central);
    }

    /// <summary>
    /// The words (and CJK/Thai trigrams) of the question that are in a small share of the library's chunks. Only these are
    /// highlighted; the search itself still uses every word. Counting stops at the limit, so a common word is cheap.
    /// </summary>
    private QueryTerms CentralTermsOf(QueryTerms terms)
    {
        if (!terms.HasWords && !terms.HasTrigrams) return terms;
        IReadOnlyList<string> Keep(IReadOnlyList<string> list, bool cjk, long chunks)
        {
            if (list.Count == 0) return list;
            var limit = (int)Math.Min(int.MaxValue, Math.Max(_options.CentralTermMinChunks, (long)Math.Ceiling(chunks * _options.CentralTermShare)));
            return list.Where(t => _chunks.CountFtsMatches(t, cjk, limit) < limit).ToList();
        }
        return new QueryTerms(
            Keep(terms.Words, false, terms.HasWords ? _chunks.CountChunks() : 0),
            Keep(terms.Trigrams, true, terms.HasTrigrams ? _chunks.CountFtsCjk() : 0));
    }

    /// <summary>
    /// A name or rare phrase can match exactly while its meaning-vector looks far from the passage, so a top keyword
    /// hit that contains every query word still counts as covered.
    /// </summary>
    private bool HasExactKeywordMatch(QueryTerms terms, IReadOnlyList<long> wordIds)
    {
        if (!terms.HasWords || wordIds.Count == 0) return false;
        var candidates = _chunks.GetPassages(wordIds.Take(10).ToList());
        return candidates.Values.Any(p => terms.Words.All(word => ExcerptBuilder.FindMatches(p.Text, new QueryTerms([word], [])).Count > 0));
    }
}
