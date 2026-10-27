using BookSearch.Core.Search;
using BookSearch.Core.Summarize;

namespace BookSearch.App.Ask;

/// <summary>Writes a question in the other languages of the library with the chat model (DCR-11).</summary>
public interface IQuestionTranslator
{
    /// <summary>
    /// The question in each of <paramref name="languages"/> (codes) that the model could translate it into; empty when it gave nothing
    /// usable (the caller then searches the question alone). Throws <see cref="TranslationUnavailableException"/> when the model could
    /// not be used (not installed, not started, a failed request or the time limit) and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    Task<IReadOnlyList<string>> TranslateAsync(string question, IReadOnlyList<string> languages, CancellationToken ct);
}

/// <summary>
/// <see cref="QueryTranslator"/> over the chat server (started on demand by <c>SupervisedChat</c>). Any failure of the model is a
/// non-critical failure: <see cref="TranslationUnavailableException"/> tells the page to search the question alone and say so.
/// Timing: see <see cref="ChatSideCall"/>.
/// </summary>
public sealed class QuestionTranslator(IChatModel chat, TimeSpan? timeout = null, Action<string>? log = null,
    Func<Task>? afterSummary = null, Func<bool>? chatReady = null) : IQuestionTranslator
{
    public Task<IReadOnlyList<string>> TranslateAsync(string question, IReadOnlyList<string> languages, CancellationToken ct) =>
        ChatSideCall.RunAsync(token => new QueryTranslator(chat).TranslateAsync(question, languages, token), "translation of the question",
            () => new TranslationUnavailableException(), timeout, log, afterSummary, chatReady, ct);
}

/// <summary>The language model could not translate the question; it is searched in its own language only.</summary>
public sealed class TranslationUnavailableException() : Exception("The question could not be translated.");
