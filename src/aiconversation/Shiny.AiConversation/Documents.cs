namespace Shiny.AiConversation;

/// <summary>
/// The session header. Small and updated in place; messages are separate append-only documents.
/// A stored session has no state: it is awaiting the user when <see cref="PendingQuestions"/> is not empty.
/// </summary>
/// <param name="Version">Optimistic concurrency token, bumped on every header save.</param>
/// <param name="Summary">The rolling compaction summary.</param>
/// <param name="LastSequence">The highest message sequence this header has seen.</param>
public record AiSessionDocument(
    string SessionId,
    int SchemaVersion,
    long Version,
    string? Title,
    DateTimeOffset Started,
    DateTimeOffset LastActivity,
    AiSessionSettings Settings,
    string? Summary,
    IReadOnlyList<AiQuestion> PendingQuestions,
    SessionUsage Usage,
    ContextSnapshot Context,
    int MessageCount,
    long LastSequence
)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// One stored message. The user message is written before the model is called and the assistant
/// message is written as <see cref="MessageStatus.Streaming"/> when the turn starts, so a crash
/// mid-turn leaves an honest record.
/// </summary>
/// <param name="Sequence">Ordering key, allocated by the store on append.</param>
/// <param name="Text">Plain text of the displayed parts, denormalized for search and display.</param>
/// <param name="SpokenText">What text-to-speech actually played, when the reply was interrupted.</param>
/// <param name="Compacted">Excluded from the prompt; kept for history and search.</param>
/// <param name="RunId">The process run that wrote the message, used for lazy crash recovery.</param>
public record AiMessageDocument(
    string MessageId,
    string SessionId,
    long Sequence,
    int SchemaVersion,
    DateTimeOffset Timestamp,
    MessageRole Role,
    MessageSource Source,
    MessageStatus Status,
    IReadOnlyList<MessagePart> Parts,
    string? Text,
    string? SpokenText,
    UsageSnapshot? Usage,
    bool Compacted,
    string? CompactedIntoMessageId,
    string RunId
)
{
    public const int CurrentSchemaVersion = 1;
}

public enum MessageRole
{
    User,
    Assistant,
    Tool,
    System,
    Summary
}

public enum MessageSource
{
    Typed,
    Voice,
    ChoiceAnswer,
    Camera,
    Interceptor,
    Compaction,
    MemoryFlush,

    /// <summary>
    /// Produced by the model.
    /// </summary>
    Model
}

public enum MessageStatus
{
    Complete,
    Streaming,
    Interrupted,
    Failed
}

public record MessagePage(IReadOnlyList<AiMessageDocument> Messages, bool HasMore);
