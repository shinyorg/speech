# Shiny.AiConversation v3 — Contracts

Companion to [aiconversation-sessions.md](aiconversation-sessions.md). Design sketch, not compiled.
Items marked `// NEW` were added while consolidating (gaps the plan referenced but never defined).

## 1. Core — manager, session, turn

```csharp
namespace Shiny.AiConversation;

// Stateless: never caches sessions. Get/GetLatest always rehydrate from the store (D9).
public interface IAiSessionManager
{
    IAiMemoryService Memory { get; }                                    // app-facing memory API (§4.8)

    Task<IAiSession> Start(AiSessionSettings? settings = null, CancellationToken ct = default);
    Task<IAiSession?> Get(string sessionId, CancellationToken ct = default);
    Task<IAiSession?> GetLatest(CancellationToken ct = default);
    Task<IReadOnlyList<AiSessionDocument>> Query(SessionQuery query, CancellationToken ct = default);   // headers only
    Task Delete(string sessionId, CancellationToken ct = default);      // cascades messages, session memory, attachments
}

// Sessions never end (D10). Dispose releases the instance, not the session.
public interface IAiSession : IAsyncDisposable
{
    string SessionId { get; }
    string? Title { get; }
    DateTimeOffset Started { get; }
    AiSessionSettings Settings { get; }
    AiSessionState State { get; }
    IReadOnlyList<AiQuestion> PendingQuestions { get; }
    SessionUsage Usage { get; }
    ContextSnapshot Context { get; }
    IAiTurn? ActiveTurn { get; }

    IAsyncEnumerable<AiSessionEvent> GetEvents(CancellationToken ct = default);   // hot broadcast, Snapshot first

    IAiTurn Send(AiInput input, CancellationToken ct = default);   // supersedes any active turn
    Task<MessagePage> GetMessages(long? beforeSequence, int count, CancellationToken ct = default);
    Task Compact(CancellationToken ct = default);

    Task SetTitle(string title, CancellationToken ct = default);                                   // NEW
    Task UpdateSettings(Func<AiSessionSettings, AiSessionSettings> update, CancellationToken ct = default);   // NEW
}

public enum AiSessionState { Idle, Thinking, Responding, AwaitingUser, Compacting }

public interface IAiTurn
{
    string TurnId { get; }
    IAsyncEnumerable<AiTurnEvent> Events { get; }       // replays from the start on re-enumeration
    Task<AiTurnResult> Completion { get; }
    void Interrupt(string? spokenText = null);
}

public record AiTurnResult(
    AiTurnOutcome Outcome,
    AiMessageDocument? Reply,
    IReadOnlyList<AiQuestion> Questions,
    UsageSnapshot? Usage,
    Exception? Error = null
);

public enum AiTurnOutcome { Completed, AwaitingUser, Interrupted, Superseded, Failed }   // NEW: Superseded = replaced by a newer Send
```

## 2. Inputs, parts, questions

```csharp
public record AiInput(IReadOnlyList<MessagePart> Parts, MessageSource Source)
{
    public static AiInput Text(string text, MessageSource source = MessageSource.Typed);
    public static AiInput Voice(string text);
    public static AiInput Answer(string questionId, params string[] choiceIds);
    public AiInput WithImage(ReadOnlyMemory<byte> data, string mediaType);
}

[Flags]
public enum Audience { None = 0, Spoken = 1, Displayed = 2, ModelOnly = 4 }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ImagePart), "image")]
[JsonDerivedType(typeof(ToolCallPart), "toolCall")]
[JsonDerivedType(typeof(ToolResultPart), "toolResult")]
[JsonDerivedType(typeof(AnswerPart), "answer")]
public abstract record MessagePart(Audience Audience);

public record TextPart(string Text, Audience Audience = Audience.Spoken | Audience.Displayed) : MessagePart(Audience);
public record ImagePart(AttachmentRef Attachment, string? Description = null, Audience Audience = Audience.Displayed) : MessagePart(Audience);
public record ToolCallPart(string CallId, string Name, string ArgumentsJson) : MessagePart(Audience.ModelOnly);
public record ToolResultPart(string CallId, string Name, string Result, bool IsError = false) : MessagePart(Audience.ModelOnly);
public record AnswerPart(string QuestionId, IReadOnlyList<string> ChoiceIds, string Text) : MessagePart(Audience.Displayed);

public record AttachmentRef(string AttachmentId, string MediaType, long Length);   // NEW (binary never inline)

// Carried over from v2 (now produced by the ask_user tool, not a JSON envelope)
public record AiQuestion(string Id, string Text, IReadOnlyList<AiChoice>? Choices = null, bool AllowMultiple = false)
{
    public bool HasChoices => this.Choices is { Count: > 0 };
}
public record AiChoice(string Id, string Label);
```

## 3. Events (`IAsyncEnumerable` only — no Rx, no C# events)

```csharp
public abstract record AiTurnEvent(string TurnId, DateTimeOffset Timestamp);
public record TurnStarted(...) : AiTurnEvent;
public record TextDelta(string Text) : AiTurnEvent;
public record ToolCallStarted(string CallId, string Name, string ArgumentsJson, bool Silent) : AiTurnEvent;
public record ToolCallCompleted(string CallId, string Name, string Result, bool IsError) : AiTurnEvent;
public record QuestionsAsked(IReadOnlyList<AiQuestion> Questions) : AiTurnEvent;
public record TurnCompleted(AiTurnResult Result) : AiTurnEvent;
public record TurnInterrupted(string? SpokenText) : AiTurnEvent;
public record TurnFailed(Exception Error) : AiTurnEvent;

public abstract record AiSessionEvent(string SessionId, DateTimeOffset Timestamp);
public record Snapshot(AiSessionState State, IReadOnlyList<AiQuestion> PendingQuestions, SessionUsage Usage, ContextSnapshot Context) : AiSessionEvent;
public record MessageAdded(AiMessageDocument Message) : AiSessionEvent;
public record MessageUpdated(AiMessageDocument Message) : AiSessionEvent;
public record StateChanged(AiSessionState State) : AiSessionEvent;
public record TurnEventOccurred(AiTurnEvent Event) : AiSessionEvent;
public record UsageChanged(SessionUsage Usage, ContextSnapshot Context) : AiSessionEvent;
public record ContextPressureChanged(ContextSnapshot Context) : AiSessionEvent;
public record ContextCompacted(AiMessageDocument Summary) : AiSessionEvent;
public record MemoryChanged(AiMemoryDocument Document, MemoryChange Change) : AiSessionEvent;
public record MemoryPressureChanged(MemoryScope Scope, string? ScopeKey, long UsedBytes, long? LimitBytes) : AiSessionEvent;
public record SessionError(Exception Error) : AiSessionEvent;

// Backpressure: TextDelta (and voice levels/interim text) are coalescable, drop-oldest.
// Everything else is lifecycle and never dropped; a subscriber that can't keep up is completed with an error.
```

## 4. Usage & context

```csharp
public record SessionUsage(            // cumulative, billed (grows forever)
    long InputTokens, long OutputTokens, long CachedInputTokens, long ReasoningTokens, long TotalTokens, int RequestCount);

public record UsageSnapshot(           // one assistant message
    long? InputTokens, long? OutputTokens, long? CachedInputTokens, long? ReasoningTokens, long? TotalTokens);

public record ContextSnapshot(         // how full the context window is now
    long CurrentTokens,                // last single request: input + output
    long? MaxTokens,
    double? PercentUsed,
    ContextPressure? Pressure,         // null when MaxTokens is unknown
    bool IsEstimate,
    DateTimeOffset MeasuredAt);

public enum ContextPressure { Normal, Warning, Critical }
```

## 5. Settings (persisted) vs runtime (DI)

```csharp
// Persisted in the session header; everything needed to rehydrate.
public record AiSessionSettings
{
    public string? Profile { get; init; }                         // selects tools/prompts via interceptors
    public int? ContextWindow { get; init; }                      // falls back to IChatClientProvider.ContextWindow
    public double ContextWarningThreshold { get; init; } = 0.75;
    public double ContextCriticalThreshold { get; init; } = 0.90;
    public CompactionSettings Compaction { get; init; } = new();
    public bool ExcludeFromSearch { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public record CompactionSettings
{
    public double? AutoCompactThreshold { get; init; } = 0.95;    // safety net (D11); null disables
    public int KeepRecentMessages { get; init; } = 10;
    public int? MaxMessages { get; init; }                        // hard cap when MaxTokens is unknown
    public bool FlushToMemory { get; init; } = true;
}

// Runtime, never persisted
public interface IChatClientProvider
{
    Task<IChatClient> GetChatClient(CancellationToken ct = default);
    int? ContextWindow => null;                                   // NEW member
    bool SupportsImagesInToolResults => false;                    // NEW member
}

public interface IContextCompactor                                 // moved out of settings into DI
{
    Task<string> Summarize(string? previousSummary, IReadOnlyList<AiMessageDocument> messages, CancellationToken ct);
}
```

## 6. Interceptors & tool registration

```csharp
public interface ISessionInterceptor
{
    Task OnSessionLoaded(SessionLoadedContext ctx) => Task.CompletedTask;     // Start AND every Get
    Task OnBeforeTurn(TurnContext ctx) => Task.CompletedTask;
    Task OnAfterTurn(TurnContext ctx, AiTurnResult result) => Task.CompletedTask;
    Task OnCompacted(CompactionContext ctx) => Task.CompletedTask;
}

public abstract class SessionContextBase                           // NEW: shared shape for the contexts
{
    public IAiSession Session { get; }
    public IList<string> SystemPrompts { get; }
    public void AddTool(AITool tool, AiToolFlags flags = AiToolFlags.None);
}
public sealed class SessionLoadedContext : SessionContextBase { public bool IsNew { get; } }
public sealed class TurnContext : SessionContextBase { public AiInput Input { get; } }
public sealed class CompactionContext { public IAiSession Session { get; } public AiMessageDocument Summary { get; } }

[Flags]
public enum AiToolFlags { None = 0, Silent = 1 }                  // Silent: no spoken "one moment", ModelOnly parts
```

## 7. Persistence

```csharp
public record AiSessionDocument(
    string SessionId,
    int SchemaVersion,
    long Version,                         // optimistic concurrency
    string? Title,
    DateTimeOffset Started,
    DateTimeOffset LastActivity,
    AiSessionSettings Settings,
    string? Summary,                      // rolling compaction summary
    IReadOnlyList<AiQuestion> PendingQuestions,
    SessionUsage Usage,
    ContextSnapshot Context,
    int MessageCount,
    long LastSequence
);

public record AiMessageDocument(
    string MessageId,
    string SessionId,
    long Sequence,                        // allocated by the store
    int SchemaVersion,
    DateTimeOffset Timestamp,
    MessageRole Role,
    MessageSource Source,
    MessageStatus Status,
    IReadOnlyList<MessagePart> Parts,
    string? Text,                         // denormalized for search/display
    string? SpokenText,                   // when interrupted
    UsageSnapshot? Usage,
    bool Compacted,
    string? CompactedIntoMessageId,
    string RunId                          // NEW: process run that wrote it (lazy crash recovery)
);

public enum MessageRole { User, Assistant, Tool, System, Summary }
public enum MessageSource { Typed, Voice, ChoiceAnswer, Camera, Interceptor, Compaction, MemoryFlush }
public enum MessageStatus { Complete, Streaming, Interrupted, Failed }

public record MessagePage(IReadOnlyList<AiMessageDocument> Messages, bool HasMore);

public interface IAiSessionStore
{
    Task CreateSession(AiSessionDocument session, CancellationToken ct = default);
    Task SaveSession(AiSessionDocument session, long expectedVersion, CancellationToken ct = default);   // throws SessionConflictException
    Task<AiSessionDocument?> GetSession(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<AiSessionDocument>> QuerySessions(SessionQuery query, CancellationToken ct = default);
    Task DeleteSession(string sessionId, CancellationToken ct = default);

    Task<AiMessageDocument> AppendMessage(AiMessageDocument message, CancellationToken ct = default);   // returns with Sequence assigned
    Task UpdateMessage(AiMessageDocument message, CancellationToken ct = default);
    Task<IReadOnlyList<AiMessageDocument>> GetMessages(MessageQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<MessageSearchHit>> SearchMessages(MessageSearchQuery query, CancellationToken ct = default);   // NEW: ranked, for §5.7

    Task Purge(DateTimeOffset before, CancellationToken ct = default);
}

public record SessionQuery(string? Text = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    IReadOnlyList<string>? Tags = null, string? Profile = null, int Skip = 0, int Take = 50);

public record MessageQuery(string SessionId, long? BeforeSequence = null, long? AfterSequence = null,
    int? Limit = null, bool IncludeCompacted = true);

public record MessageSearchQuery(string Text, string? SessionId = null, string? ExcludeSessionId = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, long? ExcludeFromSequence = null, int Limit = 10);

public record MessageSearchHit(string SessionId, string? SessionTitle, long Sequence, MessageRole Role,
    DateTimeOffset Timestamp, string Snippet, double Score);

public interface IAiAttachmentStore                                // NEW: referenced by §4.1 but never defined
{
    Task<AttachmentRef> Save(Stream content, string mediaType, string sessionId, CancellationToken ct = default);
    Task<Stream?> Open(string attachmentId, CancellationToken ct = default);
    Task Delete(string attachmentId, CancellationToken ct = default);
    Task DeleteForSession(string sessionId, CancellationToken ct = default);
}

public sealed class SessionConflictException : Exception { }
```

## 8. Memory

```csharp
public enum MemoryScope { Session, Profile, Global }
public enum MemoryAuthor { Assistant, User, App }
public enum MemoryChange { Created, Updated, Deleted, Renamed }

public record AiMemoryDocument(
    string Path, MemoryScope Scope, string? ScopeKey, string? Description, string Content,
    DateTimeOffset Created, DateTimeOffset Updated, MemoryAuthor UpdatedBy, long Version, int SchemaVersion);

public interface IAiMemoryStore
{
    Task<AiMemoryDocument?> Get(MemoryScope scope, string? scopeKey, string path, CancellationToken ct = default);
    Task<IReadOnlyList<AiMemoryDocument>> List(MemoryScope? scope = null, string? scopeKey = null, string? pathPrefix = null, CancellationToken ct = default);
    Task Save(AiMemoryDocument document, long? expectedVersion, CancellationToken ct = default);   // null = create
    Task Delete(MemoryScope scope, string? scopeKey, string path, CancellationToken ct = default);
    Task Clear(MemoryScope scope, string? scopeKey = null, CancellationToken ct = default);
    Task<long> GetScopeSize(MemoryScope scope, string? scopeKey, CancellationToken ct = default);
}

// App-facing (user must be able to see, edit and delete everything)
public interface IAiMemoryService
{
    Task<IReadOnlyList<AiMemoryDocument>> List(MemoryScope? scope = null, string? scopeKey = null, CancellationToken ct = default);
    Task<AiMemoryDocument?> Get(MemoryScope scope, string? scopeKey, string path, CancellationToken ct = default);
    Task Save(MemoryScope scope, string? scopeKey, string path, string content, string? description = null, CancellationToken ct = default);   // UpdatedBy = User/App
    Task Delete(MemoryScope scope, string? scopeKey, string path, CancellationToken ct = default);
    Task Clear(MemoryScope? scope = null, string? scopeKey = null, CancellationToken ct = default);   // "forget everything"
}

public record AiMemoryOptions
{
    public int? MaxDocumentBytes { get; init; } = 16 * 1024;
    public int? MaxIndexEntries { get; init; } = 50;
    public int? MaxIndexTokens { get; init; } = 1500;
    public long? MaxScopeBytes { get; init; } = 256 * 1024;
    public int? MaxWritesPerTurn { get; init; } = 20;
}
```

## 9. Built-in tool schemas (what the model sees)

```text
ask_user(questions: [{ id, text, choices?: [{ id, label }], allowMultiple? }])         terminating
memory(command: view|create|replace|append|delete|rename, path, scope?: session|profile|global,
       content?, old_str?, new_str?, new_path?, description?)                            silent
search_conversations(query, scope?: all|current|others, from?, to?, limit? = 10)        silent, opt-in
read_conversation(sessionId, aroundSequence, before? = 5, after? = 5)                   silent, opt-in
use_camera(purpose, mode?: photo|text|barcode|document, facing?: back|front)            opt-in (Maui.Camera)
get_available_voices / play_voice_sample / change_voice                                 opt-in (Voice)
```

## 10. Voice — `Shiny.AiConversation.Voice`

```csharp
public interface IVoiceConversation
{
    VoiceState State { get; }
    IAiSession? Session { get; }
    IAsyncEnumerable<VoiceEvent> GetEvents(CancellationToken ct = default);

    Task Attach(IAiSession session, VoiceOptions options, CancellationToken ct = default);   // one attached session at a time
    Task PushToTalk(CancellationToken ct = default);
    Task StopSpeaking();
    Task Detach();
    Task<AccessState> RequestAccess();                     // real status, not collapsed to Restricted
}

public enum VoiceState { Off, WaitingForWakeWord, Listening, FollowUp, Speaking, Paused }   // NEW: Paused (camera open)
public enum InterruptionMode { Off, QuietWords, Full }
public enum AiAcknowledgement { None, AudioBlip, LessWordy, Full }   // carried over

public record VoiceOptions
{
    public string? WakeWord { get; init; }                                         // null = off
    public InterruptionMode Interruption { get; init; } = InterruptionMode.QuietWords;
    public IReadOnlyList<string> QuietWords { get; init; } = DefaultQuietWords;
    public float InterruptionMinConfidence { get; init; } = 0.5f;                   // Full mode only
    public TimeSpan? FollowUpTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public AiAcknowledgement Acknowledgement { get; init; } = AiAcknowledgement.Full;
    public SpeechRecognitionOptions? SpeechToText { get; init; }
    public TextToSpeechOptions? TextToSpeech { get; init; }

    public static IReadOnlyList<string> DefaultQuietWords { get; } =
        ["cancel", "quiet", "shut up", "stop", "nevermind", "never mind", "hush"];
}

public abstract record VoiceEvent(DateTimeOffset Timestamp);                       // NEW: never itemized in the plan
public record VoiceStateChanged(VoiceState State) : VoiceEvent;
public record InterimTranscript(string Text) : VoiceEvent;                          // coalescable
public record FinalTranscript(string Text) : VoiceEvent;
public record SpeakingSentence(string Text) : VoiceEvent;
public record SpeechInterrupted(InterruptionMode Kind, string HeardText, string? SpokenText) : VoiceEvent;
public record AudioLevel(double Input, double Output) : VoiceEvent;                 // coalescable
public record VoiceError(Exception Error) : VoiceEvent;

public enum AiAction { Ok, Cancel, Respond, Think, Error }   // carried over
public interface ISoundProvider { Task Play(AiAction action); }

// Spike only (§6.1): local wake word so cloud STT isn't streaming all room audio
public interface IWakeWordDetector
{
    IAsyncEnumerable<string> Listen(IReadOnlyList<string> wakeWords, CancellationToken ct);
}
```

The STT/TTS engine is whatever `ISpeechToTextService` / `ITextToSpeechService` is registered in Shiny.Speech; the voice driver never checks or overrides it.

## 11. Camera — `Shiny.AiConversation.Maui.Camera`

```csharp
public enum CameraMode { Photo, Text, Barcode, Document }
public enum ImageRetention { DropAfterTurn, KeepInContext, DontStore }

public record CameraToolOptions
{
    public bool RequireConfirmation { get; init; } = true;
    public ImageRetention ImageRetention { get; init; } = ImageRetention.DropAfterTurn;
    public int MaxDimension { get; init; } = 1568;
    public IReadOnlyList<CameraMode>? AllowedModes { get; init; }   // null = every installed analyzer
}
```

## 12. Registration (sketch)

```csharp
services.AddAiConversation(o =>
{
    o.SetChatClientProvider<MyChatClientProvider>();
    o.SetSessionStore<DocumentDbAiSessionStore>();        // default: in-memory
    o.SetMemoryStore<DocumentDbAiMemoryStore>();
    o.SetAttachmentStore<FileAttachmentStore>();
    o.Memory(m => m with { MaxScopeBytes = 512 * 1024 });
    o.AddInterceptor<MyInterceptor>();
    o.AddSessionTitles();
    o.AddLongTermMemory();
    o.AddConversationSearch(profiles: ["assistant"]);
});

services.AddAiConversationVoice(v => v.AddVoiceSelectionTools());   // STT/TTS providers registered separately, explicitly
services.AddAiConversationCamera(c => c with { RequireConfirmation = true });
```
