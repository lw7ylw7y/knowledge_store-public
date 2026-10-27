using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BookSearch.Core.Summarize;

namespace BookSearch.Models;

/// <summary>Streams a reply from a chat <c>llama-server</c> token by token.</summary>
public sealed class ChatClient(HttpClient http) : IChatModel
{
    /// <summary>
    /// Yields content fragments as they arrive. Cancelling the token aborts the HTTP request, which makes the
    /// server stop generating.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        options ??= new ChatOptions();
        var body = new
        {
            model = "llm",
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true,
            temperature = options.Temperature,
            max_tokens = options.MaxTokens,
            chat_template_kwargs = new { enable_thinking = false }, // thinking off: it only adds latency
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") { Content = EmbeddingClient.JsonBody(body) };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new BookSearch.Core.ModelUnavailableException($"Could not reach the model server: {e.Message}", e);
        }
        using var _ = response;
        await EmbeddingClient.EnsureSuccess(response, "chat", ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue; // blank separators and comments
            var payload = line[5..].Trim();
            if (payload == "[DONE]") yield break;

            if (TryReadDelta(payload) is { Length: > 0 } token) yield return token;
        }
    }

    private static string? TryReadDelta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
        return choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content)
               && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;
    }
}
