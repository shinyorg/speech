namespace Shiny.AiConversation;

// Turn events: per turn, ordered (IAiTurn.Events)

public abstract record AiTurnEvent(string TurnId, DateTimeOffset Timestamp);

public record TurnStarted(string TurnId, DateTimeOffset Timestamp, AiMessageDocument UserMessage) : AiTurnEvent(TurnId, Timestamp);

/// <summary>
/// A streamed chunk of reply text. Coalescable: a slow session subscriber may miss some.
/// </summary>
public record TextDelta(string TurnId, DateTimeOffset Timestamp, string Text) : AiTurnEvent(TurnId, Timestamp);

/// <param name="Silent">True for tools that should never trigger a spoken "one moment" acknowledgement.</param>
public record ToolCallStarted(string TurnId, DateTimeOffset Timestamp, string CallId, string Name, string ArgumentsJson, bool Silent) : AiTurnEvent(TurnId, Timestamp);

public record ToolCallCompleted(string TurnId, DateTimeOffset Timestamp, string CallId, string Name, string Result, bool IsError) : AiTurnEvent(TurnId, Timestamp);

public record QuestionsAsked(string TurnId, DateTimeOffset Timestamp, IReadOnlyList<AiQuestion> Questions) : AiTurnEvent(TurnId, Timestamp);

public record TurnCompleted(string TurnId, DateTimeOffset Timestamp, AiTurnResult Result) : AiTurnEvent(TurnId, Timestamp);

/// <summary>
/// The turn was interrupted by the user or superseded by a newer <see cref="IAiSession.Send"/>.
/// </summary>
public record TurnInterrupted(string TurnId, DateTimeOffset Timestamp, AiTurnResult Result, string? SpokenText) : AiTurnEvent(TurnId, Timestamp);

public record TurnFailed(string TurnId, DateTimeOffset Timestamp, AiTurnResult Result, Exception Error) : AiTurnEvent(TurnId, Timestamp);


// Session events: everything in the session, ordered (IAiSession.GetEvents)

public abstract record AiSessionEvent(string SessionId, DateTimeOffset Timestamp);

/// <summary>
/// Always the first event of a subscription, so a late subscriber knows the current state.
/// </summary>
public record Snapshot(
    string SessionId,
    DateTimeOffset Timestamp,
    AiSessionState State,
    IReadOnlyList<AiQuestion> PendingQuestions,
    SessionUsage Usage,
    ContextSnapshot Context
) : AiSessionEvent(SessionId, Timestamp);

public record MessageAdded(string SessionId, DateTimeOffset Timestamp, AiMessageDocument Message) : AiSessionEvent(SessionId, Timestamp);

public record MessageUpdated(string SessionId, DateTimeOffset Timestamp, AiMessageDocument Message) : AiSessionEvent(SessionId, Timestamp);

public record StateChanged(string SessionId, DateTimeOffset Timestamp, AiSessionState State) : AiSessionEvent(SessionId, Timestamp);

public record TurnEventOccurred(string SessionId, DateTimeOffset Timestamp, AiTurnEvent Event) : AiSessionEvent(SessionId, Timestamp);

public record UsageChanged(string SessionId, DateTimeOffset Timestamp, SessionUsage Usage, ContextSnapshot Context) : AiSessionEvent(SessionId, Timestamp);

public record ContextPressureChanged(string SessionId, DateTimeOffset Timestamp, ContextSnapshot Context) : AiSessionEvent(SessionId, Timestamp);

public record TitleChanged(string SessionId, DateTimeOffset Timestamp, string? Title) : AiSessionEvent(SessionId, Timestamp);

public record SessionError(string SessionId, DateTimeOffset Timestamp, Exception Error) : AiSessionEvent(SessionId, Timestamp);
