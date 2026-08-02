using Microsoft.Extensions.AI;

namespace Shiny.AiConversation;

/// <summary>
/// Persists and queries chat message history. Implementations provide the underlying
/// storage mechanism (e.g. SQLite, file system, cloud).
/// </summary>
public interface IMessageStore
{
    /// <summary>
    /// Persists a completed exchange. Implementations should store <paramref name="assistantMessage"/>
    /// as the AI's message text - when structured output is in play, <c>response.Text</c> is the raw
    /// JSON envelope and persisting that would feed JSON back into future prompts and chat history.
    /// The full <paramref name="response"/> is still supplied for metadata such as token usage.
    /// </summary>
    /// <param name="userTriggeringMessage">The user message that prompted the response, if any.</param>
    /// <param name="assistantMessage">The AI's display text - the parsed reply, or the raw text when unstructured.</param>
    /// <param name="response">The underlying response, for usage and any other provider metadata.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task Store(string? userTriggeringMessage, string? assistantMessage, ChatResponse response, CancellationToken cancellationToken);
    
    /// <summary>
    /// Clears messages from the store. If <paramref name="beforeDate"/> is specified,
    /// only messages older than that date are removed; otherwise all messages are cleared.
    /// </summary>
    /// <param name="beforeDate">Optional cutoff date. Messages before this date are removed.</param>
    Task Clear(DateTimeOffset? beforeDate = null);

    /// <summary>
    /// Queries the message store with optional filtering by content, date range, and result limit.
    /// </summary>
    /// <param name="messageContains">Optional text to match against message content.</param>
    /// <param name="fromDate">Optional inclusive start date filter.</param>
    /// <param name="toDate">Optional inclusive end date filter.</param>
    /// <param name="limit">Optional maximum number of messages to return.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A list of matching messages ordered by timestamp.</returns>
    Task<IReadOnlyList<AiChatMessage>> Query(
        string? messageContains = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int? limit = null,
        CancellationToken cancellationToken = default
    );
}