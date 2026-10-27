using System.Text;
using System.Text.RegularExpressions;
using BookSearch.Core.Summarize;

namespace BookSearch.Core.Search;

/// <summary>
/// Writes the question in the other languages of the library (DCR-11), so that a passage in another language is found by a query in
/// its own language: the keyword search cannot match across languages, and the meaning search ranks same-language text higher.
/// The model sees only the question, never a result.
/// </summary>
public sealed partial class QueryTranslator(IChatModel chat)
{
    public const string SystemText = """
        You translate a search question into other languages. The translations are used to search books written in those languages.
        Rules:
        1. Translate the question, keeping its meaning, names and numbers. Do not answer it and do not add anything.
        2. Write one line for each language asked, in the order asked, starting with the name of the language and a colon.
        3. If the question is already in a language asked for, write it unchanged on that line.
        4. Reply with those lines only, with no quotes and no explanation.
        """;

    public const int MaxLength = 300;

    private static readonly ChatOptions Options = new(Temperature: 0, MaxTokens: 250);

    public static IReadOnlyList<ChatMessage> Build(string question, IReadOnlyList<string> languages) =>
    [
        ChatMessage.System(SystemText),
        ChatMessage.User($"Question: {question.Trim()}\nLanguages: {string.Join(", ", languages.Select(TranslationTargets.NameOf))}"),
    ];

    /// <summary>
    /// The translations that could be used, in the order of <paramref name="languages"/> (a language the reply left out or answered
    /// with the question itself is skipped; a reply made only of copies of the question, such as a name, gives an empty list). A model
    /// error is not caught here: the caller decides how to carry on without them. A reply with no usable line at all is an error too
    /// (<see cref="InvalidDataException"/>), so that it is logged and shown as a failure, not mistaken for "nothing to translate".
    /// </summary>
    public async Task<IReadOnlyList<string>> TranslateAsync(string question, IReadOnlyList<string> languages, CancellationToken ct = default)
    {
        if (languages.Count == 0) return [];
        var reply = new StringBuilder();
        await foreach (var token in chat.StreamAsync(Build(question, languages), Options, ct).ConfigureAwait(false)) reply.Append(token);
        var text = reply.ToString();
        if (Candidates(text, languages).Count == 0) throw new InvalidDataException("The reply has no usable translation.");
        return Parse(text, question, languages);
    }

    public static IReadOnlyList<string> Parse(string reply, string question, IReadOnlyList<string> languages)
    {
        var result = new List<string>();
        foreach (var candidate in Candidates(reply, languages))
            if (!Same(candidate, question) && !result.Any(r => Same(r, candidate))) result.Add(candidate);
        return result;
    }

    /// <summary>The usable line for each language the reply answered, in the order asked, copies of the question included.</summary>
    private static List<string> Candidates(string reply, IReadOnlyList<string> languages)
    {
        var lines = reply.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var result = new List<string>();
        for (var i = 0; i < languages.Count; i++)
        {
            var name = TranslationTargets.NameOf(languages[i]);
            var line = lines.FirstOrDefault(l => l.TrimStart('*', '-', ' ').StartsWith(name, StringComparison.OrdinalIgnoreCase)
                                                 && Label().IsMatch(l.TrimStart('*', '-', ' ')))
                       ?? (languages.Count == 1 && lines.Count == 1 ? lines[0] : null);      // a lone answer may come without its label
            if (line is not null && Clean(line) is { } text) result.Add(text);
        }
        return result;
    }

    /// <summary>The line without a language label, quotes or emphasis marks; null if nothing usable is left.</summary>
    public static string? Clean(string line)
    {
        line = Label().Replace(line.TrimStart('*', '-', ' '), "", 1).Trim(' ', '"', '\'', '`', '*', '“', '”', '‘', '’', '「', '」');
        return line.Length == 0 || line.Length > MaxLength ? null : line;
    }

    private static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static string Normalize(string text) =>
        string.Concat(NameFolding.Words(NameFolding.Fold(text)).Select(w => w + " ")).Trim();

    /// <summary>"Chinese:", "English：" and the like at the start of a line.</summary>
    [GeneratedRegex(@"^[A-Za-z]{3,12}\s*[:：]\s*")]
    private static partial Regex Label();
}
