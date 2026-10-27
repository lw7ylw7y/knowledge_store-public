using System.Runtime.CompilerServices;
using BookSearch.Core.Summarize;

namespace BookSearch.Models;

/// <summary>
/// The chat model as the rest of the app sees it, backed by <see cref="ServerSupervisor"/>: a lease on the chat server is
/// held for the whole reply (so the idle timer can't stop it mid-summary) and released when the reply ends or is cancelled.
/// </summary>
public sealed class SupervisedChat(ServerSupervisor supervisor) : IChatModel
{
    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var lease = await supervisor.AcquireAsync(ServerRole.Chat, ct).ConfigureAwait(false);
        using var http = lease.CreateHttpClient();
        await foreach (var token in new ChatClient(http).StreamAsync(messages, options, ct).ConfigureAwait(false))
            yield return token;
    }
}
