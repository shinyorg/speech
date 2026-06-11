using Microsoft.Extensions.AI;

namespace Shiny.AiConversation;

/// <summary>
/// Persists and queries chat message history. Implementations provide the underlying
/// storage mechanism (e.g. SQLite, file system, cloud).
/// </summary>
public interface IMessageStore
{
    /// <summary>
    /// Allows storing additional metadata about a chat response, such as tool calls or follow-up actions.
    /// </summary>
    /// <param name="userTriggeringMessage"></param>
    /// <param name="response"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task Store(string? userTriggeringMessage, ChatResponse response, CancellationToken cancellationToken);
    
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