namespace Shiny.AiConversation;

/// <summary>
/// Persists session headers and their messages. The store is the only source of truth: the session
/// manager keeps nothing in memory.
/// </summary>
public interface IAiSessionStore
{
    /// <summary>
    /// Creates a new session header. Throws when the id already exists.
    /// </summary>
    Task CreateSession(AiSessionDocument session, CancellationToken ct = default);

    /// <summary>
    /// Replaces the header only if the stored <see cref="AiSessionDocument.Version"/> equals
    /// <paramref name="expectedVersion"/>; otherwise throws <see cref="SessionConflictException"/>.
    /// The caller sets the new version on <paramref name="session"/>.
    /// </summary>
    Task SaveSession(AiSessionDocument session, long expectedVersion, CancellationToken ct = default);

    Task<AiSessionDocument?> GetSession(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Session headers matching the query, most recently active first.
    /// </summary>
    Task<IReadOnlyList<AiSessionDocument>> QuerySessions(SessionQuery query, CancellationToken ct = default);

    /// <summary>
    /// Deletes the header and all of its messages.
    /// </summary>
    Task DeleteSession(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Appends a message, allocating its <see cref="AiMessageDocument.Sequence"/> (unique and increasing per session).
    /// </summary>
    /// <returns>The message with its sequence assigned.</returns>
    Task<AiMessageDocument> AppendMessage(AiMessageDocument message, CancellationToken ct = default);

    /// <summary>
    /// Replaces an existing message (matched by id), e.g. to finalize a streaming reply.
    /// </summary>
    Task UpdateMessage(AiMessageDocument message, CancellationToken ct = default);

    /// <summary>
    /// Messages of one session in sequence order. With <see cref="MessageQuery.BeforeSequence"/> and a limit,
    /// returns the newest <see cref="MessageQuery.Limit"/> messages before it.
    /// </summary>
    Task<IReadOnlyList<AiMessageDocument>> GetMessages(MessageQuery query, CancellationToken ct = default);

    /// <summary>
    /// Ranked text search over user and assistant message text.
    /// </summary>
    Task<IReadOnlyList<MessageSearchHit>> SearchMessages(MessageSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Deletes sessions whose last activity is before <paramref name="before"/>.
    /// </summary>
    Task Purge(DateTimeOffset before, CancellationToken ct = default);
}

public record SessionQuery(
    string? Text = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    IReadOnlyList<string>? Tags = null,
    string? Profile = null,
    int Skip = 0,
    int Take = 50
);

public record MessageQuery(
    string SessionId,
    long? BeforeSequence = null,
    long? AfterSequence = null,
    int? Limit = null,
    bool IncludeCompacted = true
);

public record MessageSearchQuery(
    string Text,
    string? SessionId = null,
    string? ExcludeSessionId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    long? ExcludeFromSequence = null,
    int Limit = 10
);

public record MessageSearchHit(
    string SessionId,
    string? SessionTitle,
    long Sequence,
    MessageRole Role,
    DateTimeOffset Timestamp,
    string Snippet,
    double Score
);

/// <summary>
/// Thrown when a session header was changed by another instance since it was read.
/// </summary>
public sealed class SessionConflictException(string sessionId)
    : Exception($"Session '{sessionId}' was modified by another instance")
{
    public string SessionId => sessionId;
}
