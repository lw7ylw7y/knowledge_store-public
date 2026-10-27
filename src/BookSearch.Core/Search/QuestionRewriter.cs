using System.Text;
using System.Text.RegularExpressions;
using BookSearch.Core.Summarize;

namespace BookSearch.Core.Search;

/// <summary>
/// Makes a follow-up question stand on its own ("What's its shadow strength?" after "What does despair do to a person?" becomes
/// "What is despair's shadow strength?"), so that the search and the summary use that one question and nothing else from the
/// conversation. The model sees only the earlier questions, never their answers or results.
/// </summary>
public sealed partial class QuestionRewriter(IChatModel chat)
{
    public const string SystemText = """
        You rewrite the last question of a conversation so that it can be understood without the earlier questions.
        Rules:
        1. Replace pronouns and vague references (it, its, he, she, they, this, that, "the author") with what they refer to in the earlier questions.
        2. Keep the language and the meaning of the last question. Do not answer it and do not add anything that is not in the questions.
        3. If the last question already stands on its own, repeat it unchanged.
        4. Reply with the rewritten question only, on one line, with no quotes and no explanation.
        """;

    public const int MaxLength = 300;

    private static readonly ChatOptions Options = new(Temperature: 0, MaxTokens: 80);

    public static IReadOnlyList<ChatMessage> Build(string question, IReadOnlyList<string> earlier)
    {
        var user = new StringBuilder("Earlier questions:\n");
        foreach (var q in earlier) user.Append("- ").Append(q.Trim()).Append('\n');
        user.Append("\nLast question: ").Append(question.Trim());
        return [ChatMessage.System(SystemText), ChatMessage.User(user.ToString())];
    }

    /// <summary>
    /// The standalone question, or null when the model's reply is unusable (empty, much too long, or the same as the question).
    /// A model error is not caught here: the caller decides how to carry on without the rewrite.
    /// </summary>
    public async Task<string?> RewriteAsync(string question, IReadOnlyList<string> earlier, CancellationToken ct = default)
    {
        var reply = new StringBuilder();
        await foreach (var token in chat.StreamAsync(Build(question, earlier), Options, ct).ConfigureAwait(false)) reply.Append(token);
        var rewritten = Clean(reply.ToString());
        if (rewritten is null || string.Equals(Normalize(rewritten), Normalize(question), StringComparison.Ordinal)) return null;
        return rewritten;
    }

    /// <summary>First line of the reply without a label, quotes or emphasis marks; null if nothing usable is left.</summary>
    public static string? Clean(string reply)
    {
        var line = reply.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;
        line = Label().Replace(line, "").Trim().Trim('"', '\'', '`', '*', '“', '”', '‘', '’', '「', '」').Trim();
        return line.Length == 0 || line.Length > MaxLength ? null : line;
    }

    private static string Normalize(string text) =>
        string.Concat(NameFolding.Words(NameFolding.Fold(text)).Select(w => w + " ")).Trim();

    [GeneratedRegex(@"^(?:rewritten|standalone|new|last)?\s*question\s*[:：]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Label();
}
