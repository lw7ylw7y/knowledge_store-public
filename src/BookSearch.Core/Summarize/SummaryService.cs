using System.Diagnostics;
using System.Text.RegularExpressions;
using BookSearch.Core.Chunking;
using BookSearch.Core.Search;

namespace BookSearch.Core.Summarize;

public sealed record SummaryOptions
{
    /// <summary>The most tokens of a result's own text sent to the model.</summary>
    public int TokensPerPassage { get; init; } = 350;

    /// <summary>Text from before and after the result, so "he" and "that night" have something to point at. 0 sends none.</summary>
    public int NeighborTokens { get; init; } = 100;

    /// <summary>A summary of one result is 2 to 3 sentences; the cap only stops a model that does not stop.</summary>
    public ChatOptions Chat { get; init; } = new(MaxTokens: 150);

    /// <summary>The three choices of the Settings page: how much text goes to the model for each result.</summary>
    public static SummaryOptions ForLevel(SummaryContextLevel level) => level switch
    {
        SummaryContextLevel.Small => new() { TokensPerPassage = 200, NeighborTokens = 0 },
        SummaryContextLevel.Large => new() { TokensPerPassage = 500, NeighborTokens = 200 },
        _ => new(),
    };
}

public enum SummaryContextLevel { Small, Medium, Large }

/// <summary>The summary of one search result.</summary>
/// <param name="Number">The result's number in the list, from 1.</param>
/// <param name="Text">2 to 3 sentences about what the passage says on the question; empty when <paramref name="NotCovered"/>.</param>
/// <param name="NotCovered">The model found nothing in the passage that helps answer the question.</param>
/// <param name="SentText">What the model was given (the passage, trimmed, with its neighbouring sentences).</param>
public sealed partial record PassageSummary(int Number, SearchHit Hit, string Label, string Text, bool NotCovered, string SentText,
    TimeSpan TimeToFirstToken, TimeSpan Total, int PromptTokens)
{
    /// <summary>The plain-text form of a passage that is not covered, as the history stores it.</summary>
    public const string NotCoveredMessage = "This passage doesn't cover the question.";

    /// <summary>The reply, or the part of it so far, says NOT_COVERED (as the whole reply, or as its first word).</summary>
    public static bool IsNotCovered(string reply) => NotCoveredStart().IsMatch(reply);

    /// <summary>
    /// A reply that has only begun and so far could still turn out to be NOT_COVERED (so a page should not show it yet).
    /// Leading characters that are not letters or digits are ignored, as <see cref="IsNotCovered"/> ignores them.
    /// </summary>
    public static bool MayBeNotCovered(string partialReply)
    {
        if (string.IsNullOrWhiteSpace(partialReply)) return false;
        var start = 0;
        while (start < partialReply.Length && !char.IsLetterOrDigit(partialReply[start])) start++;
        if (start == partialReply.Length) return true;                    // only punctuation so far, such as "**"
        var text = partialReply[start..];
        return text.Length < SummaryPrompt.NotCoveredMarker.Length
            && SummaryPrompt.NotCoveredMarker.StartsWith(text.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One result's paragraph of the plain text the history keeps: the book and place, then the summary or the not-covered line.</summary>
    public static string Paragraph(string label, string text, bool notCovered) =>
        $"{label}: {(notCovered ? NotCoveredMessage : text)}";

    [GeneratedRegex(@"^\W*NOT[_ ]COVERED", RegexOptions.IgnoreCase)]
    private static partial Regex NotCoveredStart();
}

/// <param name="Refused">No model call was made because the search found nothing the library covers.</param>
/// <param name="Passages">One summary per result, in the order of the list (empty when <paramref name="Refused"/>).</param>
public sealed record SummaryRun(IReadOnlyList<PassageSummary> Passages, bool Refused, TimeSpan TimeToFirstToken, TimeSpan Total)
{
    /// <summary>Nothing was written: refused, or every passage was judged not to cover the question.</summary>
    public bool NotCovered => Refused || Passages.All(p => p.NotCovered);

    /// <summary>The summaries as plain text for the history: one paragraph per result, headed with its book and place.</summary>
    public string Text => string.Join("\n\n", Passages
        .Where(p => p.NotCovered || !string.IsNullOrWhiteSpace(p.Text))        // a result the model wrote nothing for has no paragraph
        .Select(p => PassageSummary.Paragraph(p.Label, p.Text, p.NotCovered)));
}

/// <summary>
/// Summarizes each search result on its own with the local model, using only that passage as context (ST-34): 2 to 3 sentences
/// about what it says on the question, or NOT_COVERED when it says nothing useful. The results are done one after another, in
/// list order.
/// </summary>
public sealed class SummaryService(IChatModel chat, ITokenizer tokenizer, ISentenceSplitter splitter, SummaryOptions? options = null,
    IChunkSource? chunks = null)
{
    private readonly SummaryOptions _options = options ?? new SummaryOptions();

    /// <summary>
    /// Summarizes <paramref name="search"/>'s results. If the search says the library doesn't cover the question (or found
    /// nothing) the model isn't called. Cancelling stops generation and throws <see cref="OperationCanceledException"/>; the
    /// results finished by then have been reported through <paramref name="onPassage"/>.
    /// </summary>
    /// <param name="onPassageStarted">The result at this index (from 0) is about to be read by the model.</param>
    /// <param name="onToken">A fragment of the reply for the result at this index (from 0).</param>
    /// <param name="onPassage">A result's summary is complete.</param>
    public async Task<SummaryRun> SummarizeAsync(string question, SearchResult search, Action<int>? onPassageStarted = null,
        Action<int, string>? onToken = null, Action<PassageSummary>? onPassage = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Enter a question.", nameof(question));
        if (search.NotCovered || search.Hits.Count == 0) return new SummaryRun([], true, TimeSpan.Zero, TimeSpan.Zero);

        var builder = new ContextBuilder(tokenizer, splitter, _options, chunks);
        var passages = new List<PassageSummary>();
        var clock = Stopwatch.StartNew();
        TimeSpan? firstToken = null;
        for (var i = 0; i < search.Hits.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            onPassageStarted?.Invoke(i);
            var hit = search.Hits[i];
            var context = await builder.BuildAsync(hit, search.Terms, ct).ConfigureAwait(false);
            var messages = SummaryPrompt.Build(question, context.Label, context.Text);
            var promptTokens = tokenizer.Count(messages.Select(m => m.Content).ToList()).Sum();

            var passageClock = Stopwatch.StartNew();
            TimeSpan? passageFirst = null;
            var reply = new System.Text.StringBuilder();
            await foreach (var token in chat.StreamAsync(messages, _options.Chat, ct).ConfigureAwait(false))
            {
                passageFirst ??= passageClock.Elapsed;
                firstToken ??= clock.Elapsed;
                reply.Append(token);
                onToken?.Invoke(i, token);
            }
            passageClock.Stop();

            var text = reply.ToString().Trim();
            var notCovered = PassageSummary.IsNotCovered(text);
            var passage = new PassageSummary(i + 1, hit, context.Label, notCovered ? "" : text, notCovered, context.Text,
                passageFirst ?? passageClock.Elapsed, passageClock.Elapsed, promptTokens);
            passages.Add(passage);
            onPassage?.Invoke(passage);
        }
        clock.Stop();
        return new SummaryRun(passages, false, firstToken ?? clock.Elapsed, clock.Elapsed);
    }
}
