namespace Shiny.AiConversation;

/// <summary>
/// A conversation with the AI. Sessions never end: they keep growing (and are compacted) until deleted.
/// Disposing releases this instance only; the session stays in the store.
/// </summary>
public interface IAiSession : IAsyncDisposable
{
    string SessionId { get; }
    string? Title { get; }
    DateTimeOffset Started { get; }
    AiSessionSettings Settings { get; }
    AiSessionState State { get; }

    /// <summary>
    /// What the AI is waiting on the user to answer. Replaced by every turn.
    /// </summary>
    IReadOnlyList<AiQuestion> PendingQuestions { get; }

    /// <summary>
    /// Everything billed in this session (grows forever).
    /// </summary>
    SessionUsage Usage { get; }

    /// <summary>
    /// How full the model's context window is right now.
    /// </summary>
    ContextSnapshot Context { get; }

    /// <summary>
    /// The turn currently running, if any.
    /// </summary>
    IAiTurn? ActiveTurn { get; }

    /// <summary>
    /// Hot broadcast of everything that happens in the session. Each call gets its own subscription,
    /// whose first item is a <see cref="Snapshot"/>. Ends when <paramref name="ct"/> is cancelled or the
    /// instance is disposed.
    /// </summary>
    IAsyncEnumerable<AiSessionEvent> GetEvents(CancellationToken ct = default);

    /// <summary>
    /// Sends input to the AI. Any active turn is superseded (cancelled) first, so a session has at most
    /// one active turn.
    /// </summary>
    IAiTurn Send(AiInput input, CancellationToken ct = default);

    /// <summary>
    /// Pages the stored history, newest page first, messages in sequence order within the page.
    /// </summary>
    /// <param name="beforeSequence">Return messages older than this sequence; null for the newest page.</param>
    /// <param name="count">The page size.</param>
    Task<MessagePage> GetMessages(long? beforeSequence, int count, CancellationToken ct = default);

    Task SetTitle(string title, CancellationToken ct = default);
    Task UpdateSettings(Func<AiSessionSettings, AiSessionSettings> update, CancellationToken ct = default);
}

public enum AiSessionState
{
    Idle,
    Thinking,
    Responding,
    AwaitingUser,
    Compacting
}
