# IAiSessionStore

Persists two document types: a small **session header** (`AiSessionDocument`, updated in place) and
append-only **messages** (`AiMessageDocument`). The store is the only source of truth; the manager
keeps nothing in memory. `InMemoryAiSessionStore` is the default (register a custom one with
`opts.SetSessionStore<T>()`). The SqliteDocDb store is being rebuilt and is not available yet.

## Interface

```csharp
public interface IAiSessionStore
{
    Task CreateSession(AiSessionDocument session, CancellationToken ct = default);
    Task SaveSession(AiSessionDocument session, long expectedVersion, CancellationToken ct = default);
    Task<AiSessionDocument?> GetSession(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<AiSessionDocument>> QuerySessions(SessionQuery query, CancellationToken ct = default);
    Task DeleteSession(string sessionId, CancellationToken ct = default);

    Task<AiMessageDocument> AppendMessage(AiMessageDocument message, CancellationToken ct = default);
    Task UpdateMessage(AiMessageDocument message, CancellationToken ct = default);
    Task<IReadOnlyList<AiMessageDocument>> GetMessages(MessageQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<MessageSearchHit>> SearchMessages(MessageSearchQuery query, CancellationToken ct = default);

    Task Purge(DateTimeOffset before, CancellationToken ct = default);
}
```

## Contract rules (implementations must follow these)

1. **`CreateSession`** throws when the id already exists.
2. **`SaveSession` is conditional:** replace the header only if the stored `Version == expectedVersion`,
   otherwise throw `SessionConflictException(sessionId)`. The caller has already set the new `Version`
   on the document - store it as-is. Use a transaction / conditional update so the check is atomic.
3. **`AppendMessage` allocates `Sequence`:** unique and increasing **per session** (1, 2, 3, ...),
   allocated atomically by the store (ignore the incoming value), and return the message with it set.
   Appending must not touch the header.
4. **`UpdateMessage`** replaces by `MessageId`.
5. **`GetMessages`** returns ascending `Sequence`. Honour `IncludeCompacted`, `AfterSequence`,
   `BeforeSequence`. With `Limit` and `BeforeSequence` (or no cursor) return the **newest** `Limit`
   messages before the cursor; with only `AfterSequence` return the oldest `Limit` after it.
6. **`QuerySessions`** orders by `LastActivity` descending; `Text` matches title/summary; `Tags` must all
   match; apply `Skip`/`Take`.
7. **`DeleteSession`** cascades to the session's messages. **`Purge`** deletes sessions whose
   `LastActivity` is before the cutoff.
8. **`SearchMessages`** searches only `User`/`Assistant` messages' `Text`, skips sessions with
   `Settings.ExcludeFromSearch`, and returns ranked `MessageSearchHit`s with a short snippet.
9. **Serialize with `AiConversationJsonContext`** (source-generated, AOT safe, camelCase, string enums;
   `MessagePart` is polymorphic with a `kind` discriminator):

```csharp
var json = JsonSerializer.Serialize(doc, AiConversationJsonContext.Default.AiMessageDocument);
var back = JsonSerializer.Deserialize(json, AiConversationJsonContext.Default.AiSessionDocument);
```

Index messages on (`SessionId`, `Sequence`) and headers on `LastActivity`.

## Documents

```csharp
public record AiSessionDocument(string SessionId, int SchemaVersion, long Version, string? Title,
    DateTimeOffset Started, DateTimeOffset LastActivity, AiSessionSettings Settings, string? Summary,
    IReadOnlyList<AiQuestion> PendingQuestions, SessionUsage Usage, ContextSnapshot Context,
    int MessageCount, long LastSequence);

public record AiMessageDocument(string MessageId, string SessionId, long Sequence, int SchemaVersion,
    DateTimeOffset Timestamp, MessageRole Role, MessageSource Source, MessageStatus Status,
    IReadOnlyList<MessagePart> Parts, string? Text, string? SpokenText, UsageSnapshot? Usage,
    bool Compacted, string? CompactedIntoMessageId, string RunId);
```

- `Parts`: `TextPart`, `ToolCallPart`, `ToolResultPart`, `AnswerPart`, each with `Audience` flags
  (`Spoken`, `Displayed`, `ModelOnly`). `Text` is the denormalized display text.
- `Status`: `Complete`, `Streaming` (written at turn start), `Interrupted`, `Failed`.
- `RunId` identifies the process that wrote the message (used to repair crashed turns on load) - persist it.
- Never store `ChatMessage` / `ChatResponse` / provider raw objects; store these documents only.
