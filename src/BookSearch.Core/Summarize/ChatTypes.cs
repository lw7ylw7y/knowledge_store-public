namespace BookSearch.Core.Summarize;

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
}

/// <summary>Writing time is proportional to length: 120 words is about 170 tokens, so 250 leaves room without letting a runaway reply go on.</summary>
public sealed record ChatOptions(double Temperature = 0.2, int MaxTokens = 250);

/// <summary>A language model that streams a reply token by token (the llama-server chat client, or a fake in tests).</summary>
public interface IChatModel
{
    /// <summary>Yields content fragments as they arrive. Cancelling the token stops generation.</summary>
    IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default);
}
