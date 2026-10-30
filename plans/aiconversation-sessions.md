# Shiny.AiConversation v3 — Session-Based Rewrite

Status: **Phases 1–4 done (2026-10-29)**; Phase 5 is later work · Branch: `v3` · Started: 2026-10-21

All contracts consolidated in [aiconversation-contracts.md](aiconversation-contracts.md); it is the source of truth when the two differ.

## Goal

Talk fluently with an AI using speech-to-text and text-to-speech. Replace almost all of the current
`Shiny.AiConversation` with a session-based design, keeping the hard-won behaviour of the current
version (see [Lessons](#1-lessons-from-the-current-version)) and dropping its structural problems.

Fluent means:

- First audio within ~1s of the model starting to answer (stream + speak sentence by sentence).
- The AI can ask something and the mic reopens for the answer without a wake word.
- The user can interrupt (quiet word or just talking over it), and the model knows what was actually heard.
- A conversation survives app restarts: sessions are persisted, resumable, and searchable.
- **Sessions never end** (D10). Like a Claude conversation window, a session just keeps going; the token
  budget and compaction keep it within the model's context. Users start a new session when they want a fresh topic.
- Long conversations don't blow the context window: context size is tracked and compacted.

## Non-goals (v3.0)

- Live video streaming to the model (snapshots only; streaming waits on a realtime backend).
- Multi-user / multi-party sessions.
- Server-side session sync (store is local; a remote store can implement the same interface later).

---

## 1. Lessons from the current version

### Carry forward (each needs a test in the new code)

| # | Behaviour | Source today |
|---|-----------|--------------|
| L1 | **Echo handling has two paths.** Interruption `Off`: mute recognition during TTS + 750ms settle window, then drain. `QuietWords` / `Full`: listen live and rely on echo cancellation (`AudioProcessingOptions.EchoCancellation` / `.VoiceChat`), turned on automatically for both modes (D15). | `SpeakWithInterruptionSupport`, `TtsEchoSettleWindow` |
| L2 | **Quiet words match only the whole utterance.** `"stop"` interrupts, `"cancel this appointment"` does not. Act on the first matching *partial*, no confidence filter. | `ListenDuringSpeech` |
| L3 | **iOS partials report `Confidence = 0` as a placeholder.** On a final, 0 = "no info", not "low". Confidence floor (default 0.5) applies only to new-utterance barge-in, only while TTS is speaking. | `ListenDuringSpeech` |
| L4 | **Drain stale recognition results and keywords before every listen.** | `ReadNextUtteranceAsync`, `WaitForKeywordAsync` |
| L5 | **Follow-up timing depends on who started the turn.** User-initiated (mic tap / wake word) waits indefinitely. AI-initiated follow-up times out (default 20s), then pending questions are cleared and the wake word is required again, so room chatter isn't taken as the answer. | `ListenAndTalk`, `RunWakeWordLoop` |
| L6 | **Each turn replaces pending questions**; anything the model stopped asking about counts as answered. | `ProcessMessage` |
| L7 | **Question density differs.** Voice: at most one question per turn. Text: several independent questions are OK. | `BuildTurnContractPrompt` |
| L8 | **Degrade, never fail.** Any unexpected model output becomes plain text; the `?`-ending heuristic is the last-resort "expects reply" signal. | `RequestTurn`, `TextEndsWithQuestion` |
| L9 | **Normal cancellation is not an error.** Subscriber exceptions are isolated and logged. Sound playback failure is never fatal. | `Raise*`, `SafePlay` |
| L10 | **Acknowledgement modes** (None / AudioBlip / LessWordy / Full) are useful and stay. | `AiAcknowledgement` |
| L11 | **UI:** typing indicator needs a heartbeat (ChatView drops it after 6s); only the *heard* side of voice becomes a user bubble; history pages by timestamp, not offset. | `AiChatSession` |
| L12 | **Store the display reply, not the raw envelope.** (Moot once the JSON envelope is gone, but the rule "never persist protocol noise as content" stays.) | `IMessageStore.Store` |

### Drop / fix

| # | Problem | Fix in v3 |
|---|---------|-----------|
| P1 | One singleton holds all conversation state as mutable fields (mode enum, 2 channels, semaphore, `lastResponseExpectedReply`, `pendingQuestions`, `currentMessages`). | Per-session state in `IAiSession`; voice loop state in `IVoiceConversation`. |
| P2 | No streaming: `GetResponseAsync` waits for the full reply before TTS. The JSON envelope makes streaming impossible. | `GetStreamingResponseAsync` + `ask_user` tool (see §3) + sentence-chunked TTS. |
| P3 | Barge-in recursion: `ProcessMessage` calls itself on a new utterance, no depth limit, while still holding the semaphore. | `turn.Interrupt(spoken)` then `session.Send(...)`; a session has at most one active turn. |
| P4 | Interrupted replies are stored and replayed as if fully spoken. | Record `SpokenText` + `WasInterrupted`; send the truncated text to the model. |
| P5 | System prompts are appended *after* the user message every turn; history is unbounded and in memory. | Prompt assembly order fixed (§5.4); context budget + compaction (§4.4). |
| P6 | Persistence saves only (user, assistant) pairs, only on success; failed calls lose the user message; tool calls are never stored. | Message-level append; user message written before the LLM call; tool calls persisted. |
| P7 | The UI rebuilds the conversation from 4 loosely related events (`AiResponded`, `SpeechOccurred`, `StatusChanged`, `ErrorOccurred`) and has to guess how they fit. | One ordered session event stream. |
| P8 | Wake word and push-to-talk are mutually exclusive modes. | One long-lived recognition loop; wake word / PTT / follow-up are states of it. |
| P9 | `VoiceSelectionContextProvider` needs a lazy `IServiceProvider` lookup to break a DI cycle. | Tools are built per session and receive the session. |
| P10 | **Bug:** `SetSoundProvider<T>()` registers `DefaultSoundPlayer`, not `T` (`AiConversationOptions.cs`). | Fix in passing (and in current v2 branch if still shipped). |
| P11 | `RequestAccess` collapses every non-available state to `Restricted`. | Pass the real `AccessState` through. |

---

## 2. Architecture

```
┌────────────────────────── Shiny.AiConversation (core, no audio) ──────────────────────────┐
│ IAiSessionManager ── creates/resumes ──► IAiSession ── one active ──► IAiTurn             │
│        │                                     │  ▲                                          │
│        ▼                                     │  └── ISessionInterceptor (start/turn/end)   │
│ IAiSessionStore (sessions + messages)  ◄─────┘       ContextBudget / IContextCompactor     │
│ IChatClientProvider (unchanged contract, minus structured-output mode)                     │
└────────────────────────────────────────────────────────────────────────────────────────────┘
        ▲ Attach(session): the caller hands over a session instance
┌──────── Shiny.AiConversation.Voice ────────┐   ┌──── Shiny.AiConversation.Maui ────┐
│ IVoiceConversation: recognition loop,       │   │ ChatView adapter: session events   │
│ wake word, PTT, follow-up, barge-in,        │   │ → bubbles 1:1; choice chips from   │
│ sentence-chunked TTS, markdown stripping,   │   │ PendingQuestions                   │
│ ISoundProvider                              │   └────────────────────────────────────┘
└─────────────────────────────────────────────┘
┌──── Stores ────────────────────────────┐   ┌──── Providers ─────────────────────────┐
│ Shiny.AiConversation.Stores.SqliteDocDb │   │ OpenAI, GitHub Copilot (+ MAUI auth)   │
│ (in-memory store ships in core)         │   │ (later) realtime backend               │
└─────────────────────────────────────────┘   └────────────────────────────────────────┘
```

Rules:

- **Core never references Shiny.Speech / Shiny.Audio.** Text chat works without audio packages.
- **The manager is stateless (decided, D9).** It never caches session instances. `Start` persists a new
  session and returns it; `Get` always makes a store call and rehydrates a fresh instance. Whoever asked
  for the instance (view model, voice driver, ChatView adapter) owns it and disposes it. See §4.6.
- **Only one session is voice-attached at a time** (the mic is a single resource). The voice driver enforces
  this, because it is the one holding an instance. Text sessions can coexist.
- **The session owns conversation state; the voice driver owns audio state.** The voice driver only talks
  to the session through its public API (`Send`, `Interrupt`, events, `PendingQuestions`).
- **Room for a realtime backend.** `IAiSession` is an interface; a realtime implementation (OpenAI
  Realtime / Gemini Live) would do its own audio and replace both the turn pipeline and the voice driver,
  while still producing the same events and persisting through the same store.

### Package layout

| Package | Contents |
|---------|----------|
| `Shiny.AiConversation` | Manager, session, turn, events, `ask_user`, context budget/compaction, interceptors, store abstraction + in-memory store, `IChatClientProvider`. Depends on `Microsoft.Extensions.AI` only. |
| `Shiny.AiConversation.Voice` | `IVoiceConversation`, sound provider, voice-selection tools. Depends on core + Shiny.Speech + Shiny.Audio. |
| `Shiny.AiConversation.Stores.SqliteDocDb` | Document DB store (replaces `MessageStores.SqliteDocDb`). |
| `Shiny.AiConversation.Maui` | ChatView adapter, choice bubbles, mic input action, memory management view. |
| `Shiny.AiConversation.Maui.Camera` | Optional `use_camera` tool (§5.8) over `Shiny.Maui.Controls.Camera` (`IMediaService` + analyzers). Separate package so the core MAUI adapter doesn't pull in the camera stack. |
| `Shiny.AiConversation.OpenAi`, `.GithubCopilot`, `.Maui.GithubCopilot` | Providers, kept with minimal changes. |

---

## 3. Questions: the `ask_user` tool replaces the JSON envelope

Drop `AiStructuredOutputMode`, `AiTurnSerializer`, the schema prompt and the 4-mode fallback.

- The model streams a **plain-text reply**.
- When it needs an answer, it calls a built-in tool:
  `ask_user(questions: [{ id, text, choices?: [{ id, label }], allowMultiple? }])`.
- The tool is **terminating** (`FunctionInvocationContext.Terminate = true`), so the turn ends after the
  call and the session enters `AwaitingUser` with `PendingQuestions` set.
- The tool description carries the density rule (L7): voice-attached sessions → "at most one question".
- The question must still be phrased in the spoken/visible reply; the tool call drives UI chips and the
  follow-up mic only.
- Fallback (L8): if a model never calls tools, a reply ending in `?` still sets `AwaitingUser` with no
  structured questions.
- Choice answers are sent as a normal user message (`AiInput.Answer(questionId, choiceIds)`), which
  renders as text and also includes a structured content part for the model.

Why this matters: streaming works, every provider supports tool calls, and history stays plain text plus
a tool call, with no JSON in the transcript.

**Confirmed (Phase 1 spike, MEAI 10.7.0):** `Terminate` ends the streaming tool loop after the `ask_user`
round trip; no second model request is made. Pinned by `MeaiBehaviourTests`.

---

## 4. Persistence & context tracking

### 4.1 Principles

- **Own the persisted schema.** Don't serialize MEAI `ChatMessage` / `AIContent` as the storage format
  directly; MEAI types change between versions and include provider-specific `RawRepresentation` /
  `AdditionalProperties`. Map to our own records, with a `SchemaVersion` on every document.
- **Two document types: session header + messages.** Messages are append-only, so a turn never rewrites
  a growing document. The header is small and updated in place (state, usage, context size).
- **Write early.** The user message is saved before the LLM call; the assistant message is saved as
  `Streaming` when the turn starts and finalized (or marked `Failed` / `Interrupted`) when it ends.
  Crash mid-turn leaves an honest record.
- **AOT/trim safe.** Source-generated `JsonSerializerContext` for every stored type (same pattern as
  `AppJsonContext` today).
- **Binary content is not inlined.** Images and audio are stored by reference (`AttachmentRef` → store-specific
  blob location), not base64 inside the message document.

### 4.2 Stored documents

```csharp
public record AiSessionDocument(
    string SessionId,
    int SchemaVersion,
    string? Title,                      // generated after the first exchange; refreshable on compaction
    DateTimeOffset Started,
    DateTimeOffset LastActivity,
    long Version,                       // optimistic concurrency; bumped on every header save
    // No persisted State: a stored session is always open. AwaitingUser = PendingQuestions.Count > 0.
    AiSessionSettings Settings,         // persisted options needed to rehydrate (see §4.6)
    string? Summary,                    // rolling summary, rewritten on each compaction (search + long-term memory)
    IReadOnlyList<AiQuestion> PendingQuestions,
    SessionUsage Usage,                 // cumulative, for billing/display
    ContextSnapshot Context,            // current context size, for budgeting
    int MessageCount,
    long LastSequence,                  // monotonically increasing message sequence
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string>? Metadata
);

public record AiMessageDocument(
    string MessageId,
    string SessionId,
    long Sequence,                      // ordering key (no more "AddTicks(1)" hacks)
    int SchemaVersion,
    DateTimeOffset Timestamp,
    MessageRole Role,                   // User | Assistant | Tool | System | Summary
    MessageSource Source,               // Typed | Voice | ChoiceAnswer | Interceptor | Compaction
    MessageStatus Status,               // Complete | Streaming | Interrupted | Failed
    IReadOnlyList<MessagePart> Parts,   // text, image ref, tool call, tool result, answer
    string? Text,                       // denormalized plain text for search & display
    string? SpokenText,                 // what TTS actually played, when interrupted
    UsageSnapshot? Usage,               // per-assistant-message usage
    bool Compacted,                     // excluded from prompt, kept for UI/search
    string? CompactedIntoMessageId      // the Summary message that replaced it
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ImagePart), "image")]
[JsonDerivedType(typeof(ToolCallPart), "toolCall")]
[JsonDerivedType(typeof(ToolResultPart), "toolResult")]   // every part also carries Audience (§4.8)
[JsonDerivedType(typeof(AnswerPart), "answer")]
public abstract record MessagePart;
```

Mapping to and from MEAI lives in one place (`MessageMapper`) and has round-trip tests.

### 4.3 Usage vs. context size: two different numbers

| | `SessionUsage` (cumulative) | `ContextSnapshot` (current) |
|---|---|---|
| Meaning | Everything billed in this session | How full the context window is right now |
| Fields | `InputTokens`, `OutputTokens`, `CachedInputTokens`, `ReasoningTokens`, `TotalTokens`, `RequestCount` | `CurrentTokens`, `MaxTokens?`, `PercentUsed?`, `Pressure?`, `IsEstimate`, `MeasuredAt` |
| Updated | After every model request (incl. each tool-loop round trip) | After every turn |
| Use | Display, cost tracking (grows forever; sessions never end) | Compaction trigger, UI meter |

How `CurrentTokens` is measured:

1. **Measured:** `InputTokens + OutputTokens` of the **last single model request** in the turn. That's
   the true size of what the provider saw, plus the reply that will be in the next prompt.
2. **Gotcha:** the turn's aggregated `ChatResponse.Usage` (and `ToChatResponse()`) sums usage across the
   tool loop, so it overstates context size. **Spike result:** while *streaming*, `FunctionInvokingChatClient`
   passes each request's `UsageContent` through unaggregated, so the turn reads per-request usage straight
   from the stream and uses the last one. No `UsageCapturingChatClient` is needed, and providers can keep
   their own `UseFunctionInvocation()` (the session adds it only when the client lacks it).
3. **Estimated:** when the provider reports no usage, estimate (chars / 4, plus fixed overhead per message
   and per tool definition) and set `IsEstimate = true`. A resumed session gets an estimate until its
   first real request.
4. **`MaxTokens`** comes from `AiSessionOptions.ContextWindow`, falling back to
   `IChatClientProvider.ContextWindow` (new optional member), else null (no percentage, no auto-compaction).

### 4.4 Context budget & compaction

Sessions never end (D10). The library's job is to **report** how full the context is; the UI around the
library decides what to tell the user (D11). Compaction is the tool to fix it, and runs automatically only
as a last resort so a request never fails for being too big.

**Context pressure (the primary mechanism)**
- `ContextSnapshot` gains `Pressure`: `Normal | Warning | Critical`, from
  `AiSessionSettings.ContextWarningThreshold` (default 0.75 of `MaxTokens`) and
  `ContextCriticalThreshold` (default 0.9).
- When the level changes, the session emits `ContextPressureChanged(snapshot)`. The level is also in the
  first `Snapshot` event, so a page that subscribes late still knows.
- **The library shows nothing.** The app warns the user ("this conversation is getting long"), offers a
  "compact" or "start a new session" action, or ignores it. `session.Compact()` is the manual action.
- With no known `MaxTokens`, there's no pressure level (`Pressure = null`); the app can still show raw
  token counts from `UsageChanged`.

**Compaction**
- `AiSessionSettings.Compaction`: `AutoCompactThreshold` (default 0.95 of `MaxTokens`; null disables it),
  `KeepRecentMessages` (default 10), `Compactor` (default: LLM summary).
- Auto-compaction is a **safety net**, not the normal path. It only exists so a never-ending session
  doesn't start failing requests once it exceeds the model's window. Apps that warn at 75% and let the user
  act will rarely hit it.
- Auto-compaction is checked **before** a turn is sent, and also after a turn completes, so the next voice
  turn doesn't pay for compaction while the user waits. If a turn's own output pushes it over, compaction
  runs in the background right after `TurnCompleted`; `Send` waits for it only if it is still running.
- Manual `Compact()` or over the auto threshold → `IContextCompactor.Compact(session)`:
  - **Rolling summary.** The previous summary plus the oldest non-compacted messages are folded into one
    new `Role = Summary` message. Summaries never pile up: the prompt always holds at most one summary.
    The previous summary message is marked `Compacted` too.
  - The header's `Summary` is updated to the new summary text (used for session search and long-term memory).
  - Marks the originals `Compacted = true` (still stored, searchable, shown in UI history).
  - Never splits a tool call from its result; never compacts an active turn or pending questions.
  - Emits a `ContextCompacted` session event, then `OnCompacted` interceptors run.
- Prompt assembly only uses the current summary plus non-compacted messages.
- A compaction failure degrades to a hard trim (drop oldest non-summary messages), never a failed turn (L8).
- **No `MaxTokens` known** → no pressure level and no automatic compaction. Optional message-count cap
  (`Compaction.MaxMessages`, default null) for apps that want a hard bound anyway.
- The summary prompt must keep facts the user will expect the AI to remember: names, preferences,
  decisions, open tasks, and unanswered questions.

### 4.5 Store abstraction

```csharp
public interface IAiSessionStore
{
    Task SaveSession(AiSessionDocument session, CancellationToken ct = default);
    Task<AiSessionDocument?> GetSession(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<AiSessionDocument>> QuerySessions(SessionQuery query, CancellationToken ct = default);
    Task DeleteSession(string sessionId, CancellationToken ct = default);   // cascades messages

    Task AppendMessage(AiMessageDocument message, CancellationToken ct = default);
    Task UpdateMessage(AiMessageDocument message, CancellationToken ct = default);  // finalize / compaction flag
    Task<IReadOnlyList<AiMessageDocument>> GetMessages(MessageQuery query, CancellationToken ct = default);
    // MessageQuery: SessionId?, Text?, From?, To?, BeforeSequence?, Limit?, IncludeCompacted

    Task Purge(DateTimeOffset before, CancellationToken ct = default);
}
```

- `SessionQuery`: text (title / summary / message text), date range, tags, state, paging.
- Implementations: `InMemoryAiSessionStore` (core, for tests and no-persistence apps) and
  `DocumentDbAiSessionStore` (SqliteDocDb).
- **Streaming writes are throttled.** A `Streaming` message is not rewritten per token; it's finalized once
  (optional checkpoint every N seconds for crash recovery of long replies).

### 4.6 Lifecycle & recovery

The store is the only source of truth. The manager holds no sessions, no registry, no "current" pointer.

**Creating and loading**
- `Start(settings)` writes the header document, then returns a new instance. The manager keeps nothing.
- `Get(id)` always reads the store: the header, plus the prompt working set (non-compacted messages and
  summaries). It restores `PendingQuestions`, re-estimates context, and returns a **new instance every
  call**. `GetLatest()` does the same for the most recently active session ("continue where I left off").
- UI history (`GetMessages`) pages straight from the store and is never cached on the instance.

**What a live instance holds**
- Only what a turn needs while the instance is alive: the header and the prompt working set. Dispose
  releases it. Nothing is registered in DI; the manager is a stateless singleton.

**Everything must be rebuildable from storage**
- `AiSessionOptions` splits in two:
  - `AiSessionSettings` (persisted in the header): context window, compaction settings,
    voice/text mode hint, tags, metadata, and an optional `Profile` name.
  - Runtime pieces (tools, interceptors, chat client) are **not** persisted. They come from DI
    interceptors on every rehydrate. An interceptor can use `Profile` to pick which tools/prompts apply
    (e.g. `"cooking"` vs `"support"`).
- Consequence: tools can't be passed straight to `Start` as an `AITool[]`, because they wouldn't come back
  on `Get`. Tools come only from interceptors (optionally keyed by `Profile`).

**Two instances of the same session**
- Because `Get` returns a new instance every time, two instances can exist for one session id (e.g. the
  chat page and the voice driver each load it). Rules:
  - The header has a `Version`. Every header save is conditional on the version the instance last read.
  - Message `Sequence` is allocated by the store at append time (unique per session), so two writers can
    never produce duplicate ordering.
  - Before each `Send`, the instance checks the header version. If someone else changed it, the instance
    **reloads its working set first** and then proceeds, rather than failing.
  - Two simultaneous turns on one session: the second header save fails the version check, and that turn
    gets a `SessionConflictException`. Sharing one instance (e.g. pass the voice driver's instance to the
    chat page) avoids this entirely, and the docs should recommend it.
- **Spike (done, Phase 2, Shiny.DocumentDb 10.0.2):** `MapVersionProperty` gives a database-checked
  optimistic update (`ConcurrencyException`), and `CreateUnitOfWork().SaveChanges()` is atomic (a version
  conflict rolls back the whole unit). The store wraps the header in a versioned document and allocates
  sequences from a versioned per-session counter written in the same unit as the message. No contract change.

**Recovery is lazy, at load time (no start-up sweep)**
- There's no registry to say whether a session is "live", so recovery happens in `Get`:
  - Each instance has a `RunId` (per process start). A `Streaming` message written by a different `RunId`
    than the current process is from a dead process → mark it `Interrupted` on load.
  - A `Streaming` message from the *current* `RunId` belongs to another live instance in this process →
    leave it alone.

**No ending** (D10)
- There's no `End()`, no `Ended` timestamp, no idle timeout and no end-of-session hook. A session is just
  a growing, compacted conversation that can be picked up again at any time with `Get` / `GetLatest`.
- Removing a session is a user action: `Delete(id)` (cascades messages). Storage growth is controlled
  with `Purge(before)` on the store and by `Delete`, never by the session itself.
- Work that used to be planned for "session end" moves:
  - **Title:** generated after the first exchange (`OnAfterTurn` on turn 1), optionally refreshed on compaction.
  - **Summary / long-term memory:** the rolling compaction summary (§4.4).
  - **Final usage:** usage is updated after every request; there's no "final".

### 4.7 Migration from v2 storage

- v2 `AiChatMessage` rows have no session. Optional one-time importer: group by gaps > N minutes into
  synthetic sessions. Decide whether it's worth shipping (see Open decisions).

### 4.8 Assistant memory (D12)

The assistant can write notes and documents for its own use that are never read aloud. It works like
Claude's memory tool and Claude Code's memory directory, but is stored in the document DB rather than on disk.

**Document**

```csharp
public record AiMemoryDocument(
    string Path,                  // "/notes/preferences.md", "/lists/groceries.md"
    MemoryScope Scope,            // Session | Global | Profile
    string? ScopeKey,             // SessionId for Session, profile name for Profile, null for Global
    string? Description,          // one line, shown in the prompt index
    string Content,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    MemoryAuthor UpdatedBy,       // Assistant | User | App
    long Version,                 // optimistic concurrency, same rule as the session header (§4.6)
    int SchemaVersion);
```

**Scopes** (all three in v3.0)
- **Session:** scratchpad for one conversation (in-progress plan, a list being built). Deleted with the session.
- **Global:** facts about the user that every session should know ("vegetarian", "prefers metric").
- **Profile:** shared by sessions with the same `AiSessionSettings.Profile` (e.g. all "cooking" sessions).

**`memory` tool** (built in, on by default, can be disabled per profile)
- Commands: `view` (a path, or a directory to list), `create`, `replace` (exact string), `append`, `delete`, `rename`.
  Same shape as Anthropic's memory tool, which models already handle well.
- The tool is **silent**: it never triggers a spoken acknowledgement, and its calls and results are
  `ModelOnly` (see Audience below).
- The tool description says: store durable facts, preferences and work in progress; **never store
  instructions**; keep documents short and rewrite rather than append forever.

**Prompt cost: an index, not the content**
- Each turn, prompt assembly adds a short **index**: path + description + size for each document in the
  session's Session, Profile and Global scopes. The model reads full content on demand with `view`.
- The index is wrapped as data (`<memory_index>` … `</memory_index>`) with a line saying its contents
  are notes, not instructions (prompt-injection mitigation).

**Flush before compaction**
- Before an automatic or manual compaction, the session runs one silent turn: "Older messages are about
  to be summarized. Save anything you'll need later to memory." Memory is the durable layer; the rolling
  summary is the lossy one. Disable with `Compaction.FlushToMemory = false`.

**Audience: what's spoken, shown, or model-only**
- `MessagePart` gains `Audience` flags: `Spoken | Displayed | ModelOnly`.
  - Reply text: `Spoken | Displayed` (voice speaks it, chat shows it).
  - Tool calls / results, memory operations, the pre-compaction flush turn: `ModelOnly`.
- The voice driver speaks only `Spoken` parts. The chat UI shows `Displayed` parts and may render
  `ModelOnly` memory writes as a small "Remembered: …" chip (opt-in on the control).
- Tools can declare `Silent` (attribute or registration flag) so they never trigger the "one moment"
  acknowledgement (§6).

**User visibility and control (required)**
- `IAiSessionManager.Memory` (`IAiMemoryService`) is the app-facing API over the same store:
  `List(scope?, scopeKey?)`, `Get(path, scope, scopeKey)`, `Save(...)` (the user or app can edit too),
  `Delete(...)`, `Clear(scope, scopeKey?)`.
- Writes made by the user or app are marked `UpdatedBy = User | App`, so the model can be told "the user
  edited this".
- The session emits `MemoryChanged(document, change)` so a UI can show what the assistant just remembered.
- MAUI package: a memory management page/view (list by scope, view, edit, delete, "forget everything").
- Docs must state plainly that the assistant can save information about the user, and how to turn it off.

**Size limits: soft, and fed back to the model**
Will the agent overflow it? Not in normal use, and storage isn't the real risk. The risks are:
1. **Append-forever patterns** in a never-ending session (e.g. logging every turn into one note).
2. **The prompt index growing** with document count, which costs tokens on *every* turn.
3. **`view` of a huge document** filling a single request's context.
4. **A misbehaving model or tool loop** writing repeatedly.

So the limits protect the context, not the disk, and they return tool feedback instead of failing:

| Limit | Default | When exceeded |
|---|---|---|
| Per-document size | 16 KB | `create` / `append` / `replace` returns a tool error: "document is over 16 KB; condense it first". `view` of an oversized document (e.g. edited by the app) returns it in pages. |
| Prompt index | 50 entries / ~1.5K tokens | Most recently updated entries first, then "N more; use `view /` to list". |
| Per-scope total | 256 KB | Write refused with a tool error asking the model to consolidate or delete; the session emits `MemoryPressureChanged` so the app can warn the user (same report-and-warn approach as D11). |
| Writes per turn | 20 | Further memory calls in that turn return an error (stops tool loops). |

All are configurable via `AiMemoryOptions`; null disables a limit.

**Storage**
- `IAiMemoryStore` is a separate interface from `IAiSessionStore` (different query patterns), but the
  SqliteDocDb package implements both.
- Binary content never goes into memory documents; it goes through the attachment references (§4.1).

---

## 5. Core API

### 5.1 Manager / session / turn

```csharp
public interface IAiSessionManager
{
    // Stateless: no caching, no Current. Every Get/GetLatest is a store call that returns a new instance (§4.6).
    Task<IAiSession> Start(AiSessionSettings? settings = null, CancellationToken ct = default);
    Task<IAiSession?> Get(string sessionId, CancellationToken ct = default);
    Task<IAiSession?> GetLatest(CancellationToken ct = default);
    Task<IReadOnlyList<AiSessionDocument>> Query(SessionQuery query, CancellationToken ct = default);   // headers only, no rehydrate
    Task Delete(string sessionId, CancellationToken ct = default);
}

public interface IAiSession : IAsyncDisposable
{
    string SessionId { get; }
    AiSessionState State { get; }       // Idle | Thinking | Responding | AwaitingUser | Compacting
    DateTimeOffset Started { get; }
    IReadOnlyList<AiQuestion> PendingQuestions { get; }
    SessionUsage Usage { get; }
    ContextSnapshot Context { get; }
    IAiTurn? ActiveTurn { get; }

    // Hot broadcast: each call gets its own subscriber stream (see §5.3). Ends when cancelled or the instance is disposed.
    IAsyncEnumerable<AiSessionEvent> GetEvents(CancellationToken ct = default);

    IAiTurn Send(AiInput input, CancellationToken ct = default);   // cancels any active turn (barge-in semantics)
    Task<MessagePage> GetMessages(long? beforeSequence, int count, CancellationToken ct = default);
    Task Compact(CancellationToken ct = default);   // manual trigger
}

public interface IAiTurn
{
    string TurnId { get; }
    IAsyncEnumerable<AiTurnEvent> Events { get; }
    Task<AiTurnResult> Completion { get; }          // final assistant message + questions + usage
    void Interrupt(string? spokenText = null);      // voice driver passes how far TTS got
}
```

### 5.2 Inputs and events

```csharp
public record AiInput(IReadOnlyList<MessagePart> Parts, MessageSource Source)
{
    public static AiInput Text(string text, MessageSource source = MessageSource.Typed);
    public static AiInput Voice(string text);
    public static AiInput Answer(string questionId, params string[] choiceIds);
    public AiInput WithImage(ReadOnlyMemory<byte> data, string mediaType);
}

// Turn events (per turn, ordered)
TurnStarted · TextDelta(string Text) · ToolCallStarted(name, args) · ToolCallCompleted(name, result)
· QuestionsAsked(questions) · TurnCompleted(result) · TurnInterrupted(spokenText) · TurnFailed(exception)

// Session events (everything, ordered: what the UI binds to)
MessageAdded(message) · MessageUpdated(message) · StateChanged(state) · TurnEvent(turnId, AiTurnEvent)
· UsageChanged(usage, context) · ContextPressureChanged(context) · ContextCompacted(summaryMessage) · Error(exception)
```

### 5.3 Event delivery: `IAsyncEnumerable` only (no Rx)

Decided (D3): every event surface is `IAsyncEnumerable<T>`. No `IObservable`, no Rx dependency, and no
C# `event`s on the new public API.

- **Turn events** (`IAiTurn.Events`): single-consumer, cold-ish stream for that turn. It completes with
  `TurnCompleted` / `TurnInterrupted`, or completes with an exception on `TurnFailed`. A second
  enumeration replays the turn's buffered events from the start (the turn buffers its own events; turns
  are short). *(Implemented: the stream ends normally after `TurnFailed`, and the error is on the result.)*
- **Session and voice events** (`IAiSession.GetEvents(ct)`, `IVoiceConversation.GetEvents(ct)`):
  **hot broadcast.** Each call registers a subscriber backed by its own `Channel<T>`. The UI, the voice
  driver and app code can all listen at once without interfering with each other.
  - Late subscribers get only new events, but the first item is a `Snapshot` event (state, pending
    questions, usage, context) so a UI never has to read properties and subscribe separately.
  - The subscription ends when the `CancellationToken` is cancelled or the instance is disposed (stream
    completes normally). Sessions never end, so there's no "session ended" completion.
  - **Backpressure:** each subscriber channel is bounded (default 256). `TextDelta` / interim-transcript /
    level events are coalescable and use drop-oldest; lifecycle events (`MessageAdded`, `StateChanged`,
    `TurnCompleted`, …) are never dropped. If a lifecycle event can't be written, the slow subscriber is
    completed with an error rather than blocking the session. A slow UI must never stall the conversation (L9).
  - Internal helper: `BroadcastChannel<T>` in core, shared by the session and the voice driver, with tests
    for concurrent subscribe/unsubscribe and completion.
- **MAUI:** the ChatView adapter runs one `await foreach` over `session.GetEvents()` on a background task
  and marshals to the UI thread; it's cancelled when the page/session is disposed.

### 5.4 Prompt assembly order (fixes P5)

1. System prompts from interceptors (`OnSessionLoaded`, `OnBeforeTurn`).
2. Mode instructions (voice vs text: plain prose, no markdown when spoken, one question max).
3. Long-term memory (other sessions' rolling summaries, if enabled).
4. This session's rolling summary from compaction.
5. Non-compacted history in sequence order (interrupted assistant messages use `SpokenText` plus a
   `[interrupted by user]` marker).
6. The new user input.

### 5.5 Interceptors (replace `IContextProvider`)

```csharp
public interface ISessionInterceptor
{
    // Runs on Start AND on every Get (rehydrate): tools, system prompts, memory. ctx.IsNew tells them apart.
    Task OnSessionLoaded(SessionLoadedContext ctx) => Task.CompletedTask;
    Task OnBeforeTurn(TurnContext ctx) => Task.CompletedTask;            // RAG, time/location, extra tools
    Task OnAfterTurn(TurnContext ctx, AiTurnResult result) => Task.CompletedTask;   // e.g. title after turn 1
    Task OnCompacted(CompactionContext ctx) => Task.CompletedTask;       // new rolling summary available
    // No OnSessionEnd: sessions never end (D10).
}
```

The contexts expose the `IAiSession`, so tools close over the session instead of a global service (fixes P9).

### 5.6 Built-in tools / interceptors

| Tool / interceptor | Package | Notes |
|---|---|---|
| `ask_user` | core | Always on (§3). |
| `memory` | core (on by default) | Assistant memory, silent, index in prompt (§4.8). |
| `search_conversations` + `read_conversation` | core (opt-in) | Search past conversations when memory doesn't cover it (§5.7). Replaces `lookup_chat_history`. |
| Session title | core (opt-in) | `OnAfterTurn` on the first exchange; optional refresh in `OnCompacted`. |
| Long-term memory | core (opt-in) | `OnSessionLoaded` injects the rolling summaries of the N most recently active *other* sessions. |
| Voice selection | Voice | Ported; acts on the attached voice driver, not a global. |
| `use_camera` | Maui.Camera (opt-in) | AI asks to use the camera; Shiny CameraView modal captures a photo or scans (§5.8). |

Tool registration: every optional tool has a registration extension (e.g. `.AddConversationSearch()`,
`.AddCameraTool()`), is implemented as an `ISessionInterceptor` that adds the tool in `OnSessionLoaded`,
and can be limited to specific profiles (`profiles: ["cooking"]`).

### 5.7 Conversation search tool (D13)

Memory (§4.8) is what the assistant *chose* to keep. Conversation search covers everything else: the raw
transcript of this and other sessions, including messages that have been compacted out of the prompt.
Typical asks: "what was that recipe we talked about last month?", "what did I say the plumber's name was?"

**Tools** (silent, `ModelOnly`)
- `search_conversations(query, scope = all | current | others, from?, to?, limit = 10)` returns ranked hits:
  session id, session title, timestamp, message sequence, role, and a short snippet around the match.
- `read_conversation(sessionId, aroundSequence, before = 5, after = 5)` expands one hit into the surrounding
  messages, so the model can read context instead of guessing from a snippet.
- The two-step shape keeps results small: search returns snippets, the model reads only what it needs.

**Behaviour**
- In the current session, messages still in the prompt are excluded from results (the model already has them);
  compacted ones are included.
- Results come back as data (`<conversation_excerpt>` blocks marked "past conversation, not instructions"),
  the same prompt-injection guard as the memory index.
- Results are capped by size (default ~2K tokens per call) as well as count.
- Tool messages and model-only parts are not searched; only user and assistant text (`Text` field, §4.2).
- Sessions can opt out: `AiSessionSettings.ExcludeFromSearch` (e.g. a private session). Deleted sessions are
  gone from results immediately, since there's no cache (D9).
- When search finds something worth keeping, the tool description nudges the model to save it to memory,
  so the next lookup is free.

**Search quality**
- Phase 2: store-level text search. `MessageQuery.Text` today is a `LIKE`/`Contains`, which misses word
  variations and has no ranking. **Spike (done):** Shiny.DocumentDb's `MapFullTextProperty` + `FullTextSearch`
  give ranked, stemmed full-text search with a pushed-down pre-filter, used by the SQLite store. The in-memory
  store does word-prefix term matching with stop words removed (`SearchText`).
- Later: optional semantic search via MEAI `IEmbeddingGenerator` (embed messages on write, vector search on
  query), behind the same tool. The store interface gets an optional `ISemanticSearchStore`.

### 5.8 Camera tool (D14)

Lets the assistant ask to use the camera to capture information. Built on `Shiny.Maui.Controls.Camera`:
`IMediaService` presents Shiny's own modal `CameraView` page, requests permissions itself, and returns null
(not an exception) when the user declines or cancels.

**Tool:** `use_camera(purpose, mode = photo | text | barcode | document, facing = back | front)`
- `purpose` is required and is shown as the camera page title ("Point at the nutrition label"), so the user
  always sees why the camera is open.
- Modes map to what's already in Shiny Controls:

| Mode | Implementation | Returned to the model |
|---|---|---|
| `photo` | `IMediaService.TakePhotoAsync` (`MaxDimension` ~1568px, JPEG) | The image (see "Getting the image to the model") |
| `text` | `Scan…` with the `.Ocr` analyzer | Recognized text only, which is far cheaper than an image |
| `barcode` | `ScanBarcodeAsync` (`.Barcode`) | Code value + format |
| `document` | `.Documents` / `AiDocumentAnalyzer` (`.Camera.Ai`) | Extracted document text/fields + optionally the page image |

- Each mode is only offered if its analyzer package is installed; the tool's schema lists only available modes.

**Consent: two layers**
1. **Conversational:** the tool description tells the model to ask first ("Can I use your camera to read
   the label?") via `ask_user` with Yes/No choices, unless the user already asked it to use the camera.
   `CameraToolOptions.RequireConfirmation` (default true) enforces it: if the last user answer wasn't a yes
   to a camera question, the tool returns "ask the user for permission first" without opening anything.
2. **System:** the OS camera permission prompt, plus the modal itself. The full-screen camera page is the
   visible indicator, and the user can always cancel.
- Outcomes returned to the model as plain results: `captured`, `cancelled by user`, `permission denied`,
  `camera unavailable`. None of these are errors (L8).

**Getting the image to the model**
- Some providers accept images inside tool results and many don't. `IChatClientProvider` gets
  `SupportsImagesInToolResults` (default false).
  - Supported: the tool result carries the image directly.
  - Not supported: the tool returns "photo captured, attached below" and **ends the tool loop**. The
    session then sends the image as a follow-up user message part (`Source = Camera`, `Displayed`) in the
    same turn, so it looks like one continuous answer to the user.
- Images are stored as attachment references (§4.1), never inline.
- **Context cost:** an image is expensive and sits in the prompt until compacted. `CameraToolOptions.ImageRetention`:
  - `DropAfterTurn` (default): after the turn, the image part is replaced in future prompts by the model's
    own description of it. The image stays in storage and is still shown in the chat UI.
  - `KeepInContext`: the image stays in the prompt until compaction.
  - `DontStore`: the image is used for that turn only and never persisted (privacy-sensitive apps).

**Voice sessions**
- When the camera opens, the voice driver stops any TTS and pauses barge-in listening (the camera page owns
  the screen; the user is pointing the phone). It resumes when the modal closes.
- The assistant's reply is spoken after the capture as usual.
- Later: voice shutter ("take it") while the camera is open. Needs the voice driver and the camera page to
  share the mic; out of scope for v3.0.

**Platform scope**
- MAUI first. Blazor later through `Shiny.Blazor.Controls.Camera`, which has the same `IMediaService` shape.
- Live video streaming stays a non-goal (realtime backend only, Phase 5).

---

## 6. Voice driver (`Shiny.AiConversation.Voice`)

```csharp
public interface IVoiceConversation
{
    VoiceState State { get; }            // Off | WaitingForWakeWord | Listening | FollowUp | Speaking
    IAiSession? Session { get; }
    IAsyncEnumerable<VoiceEvent> GetEvents(CancellationToken ct = default);   // interim transcript, levels, state (hot broadcast, §5.3)

    Task Attach(IAiSession session, VoiceOptions options, CancellationToken ct = default);
    Task PushToTalk(CancellationToken ct = default);    // works with or without wake word enabled
    Task StopSpeaking();
    Task Detach();
    Task<AccessState> RequestAccess();
}

public record VoiceOptions
{
    public string? WakeWord { get; init; }                         // null = off (push-to-talk only)
    public InterruptionMode Interruption { get; init; } = InterruptionMode.QuietWords;   // D15
    public IReadOnlyList<string> QuietWords { get; init; } = DefaultQuietWords;
    public float InterruptionMinConfidence { get; init; } = 0.5f;   // L3, Full mode only
    public TimeSpan? FollowUpTimeout { get; init; } = TimeSpan.FromSeconds(20);  // L5
    public AiAcknowledgement Acknowledgement { get; init; } = AiAcknowledgement.Full;  // L10
    public SpeechRecognitionOptions? SpeechToText { get; init; }
    public TextToSpeechOptions? TextToSpeech { get; init; }
}
```

Behaviour:

- **One long-lived recognition session** while attached; keyword and result events feed a single loop
  (fixes P8). Drain before each listen (L4).
- **State machine:** `WaitingForWakeWord` → (keyword / PTT) → `Listening` → final utterance →
  `session.Send(AiInput.Voice(text))` → `Speaking` → if session is `AwaitingUser` → `FollowUp` (timeout,
  L5) else back to `WaitingForWakeWord` / `Off`.
- **Sentence-chunked TTS:** consume `TextDelta`, split at sentence boundaries (`. ! ? 。` + newline,
  ignoring decimals and abbreviations), speak in order, start the next sentence when the previous one ends.
  Track the spoken character offset for `Interrupt(spokenText)`. Later: use `SynthesizeToStreamAsync` for
  gapless playback when `CanSynthesizeToStream`.
- **Markdown / formatting stripping** before TTS (`**`, lists, headings, code fences, links → their text).
- **Interruption modes (D15):**

| Mode | While the AI is speaking | Notes |
|---|---|---|
| `Off` | Mic muted, then the 750ms settle window and drain (L1) | Playback-only setups, or apps that don't want voice processing on during playback |
| `QuietWords` (**default**) | Listens; only an utterance that is *entirely* a quiet word ("stop", "cancel", "shut up"…) interrupts (L2). Acts on the first matching partial. | Low false-trigger risk: the AI's own voice almost never comes back as exactly "stop" |
| `Full` | Any speech interrupts and becomes the next message. Quiet words still just stop. Confidence floor applies (L3). | Highest false-trigger risk on speakerphone; best with headphones |

  - `QuietWords` and `Full` turn on echo cancellation automatically. **iOS trade-off:** voice processing
    during playback can make TTS quieter or change the audio route compared with plain playback. Apps
    that care use `Off`. Document this.
  - Quiet word → `turn.Interrupt(spoken)` and go idle. New utterance (`Full`) → `turn.Interrupt(spoken)`
    then `session.Send(...)` (no recursion, P3).
  - The quiet-word list must not be "inert": with `Off`, quiet words are ignored and the docs say so
    (fixes the v2 trap where a default list existed but `InterruptionEnabled` was false).
- **Wake word** stays off by default (`WakeWord = null`): only the app can choose the word, and an
  always-listening mic has battery, privacy and (with cloud STT) cost implications. See "Speech engines".
- **Sounds:** `ISoundProvider` (fix P10); failures never fatal (L9).
- Tool calls during voice can trigger an optional acknowledgement ("one moment") so silence isn't mistaken
  for a hang. Tools marked `Silent` (including `memory`) never do.
- Only parts with `Audience.Spoken` are sent to TTS (§4.8).

### 6.1 Speech engines (D16)

The voice driver uses this repo's `ISpeechToTextService` / `ITextToSpeechService` (Shiny.Speech) and is
provider-agnostic: Azure, OpenAI, ElevenLabs, Typecast, Microsoft.Extensions.AI, Linux Whisper, or native.

- **The engine is whatever `ISpeechToTextService` / `ITextToSpeechService` the app registers** through the
  Shiny.Speech abstraction. The voice driver doesn't inspect, validate or override it.
  - The voice package does **not** auto-register native speech (drop v2's `AutoAddSpeechServices`). The app
    registers its STT/TTS providers explicitly.
  - Our apps won't use Android's native `SpeechRecognizer` (its quality isn't good enough for
    conversation); that's done by registering a cloud provider, and the docs recommend the same.
- **Wake word with cloud STT is expensive.** Cloud keyword detection (`CloudSpeechToText`) works by
  transcribing everything and regex-matching finals, so an always-on wake word streams all room audio to
  the cloud: per-minute cost, battery, and privacy. Options, in order of preference:
  1. **Local gate:** a voice-activity detector on the `IAudioSource` stream so audio is only sent to the
     cloud while someone is speaking. Cuts cost; doesn't fix privacy.
  2. **Local keyword spotter** (on-device model) that only opens the cloud stream after the wake word.
     Fixes cost and privacy; needs a new pluggable `IWakeWordDetector` and a model/library choice. Spike.
  3. Accept the cost: document it, and prefer push-to-talk on Android.
  Until 1 or 2 exist, wake word on Android with cloud STT is supported but documented as costly.
- **Turn-taking latency with cloud STT:** end-of-utterance comes from the provider's silence endpointing.
  Expose the provider's endpoint/silence setting through `SpeechRecognitionOptions` where available, and
  measure end-of-speech → first-audio as the key latency metric in the sample app.
- **Cloud TTS latency:** sentence-chunked TTS (above) with a cloud voice adds a network round trip per
  sentence. Synthesize sentence N+1 (`SynthesizeToStreamAsync`) while sentence N is playing, so playback is
  gapless. This moves "gapless TTS" from Phase 5 into Phase 3 for cloud voices.
- **Follow-up and quiet-word listening during TTS** hold the cloud STT stream open while the AI speaks, which
  is billed time. Acceptable for `QuietWords`; noted in the docs.

---

## 7. MAUI adapter

- `IChatSession` implementation bound to an `IAiSession`; maps `MessageAdded` / `MessageUpdated` 1:1 to
  bubbles (fixes P7). Streaming replies update one bubble in place via `MessageUpdated`.
- Typing heartbeat (L11); choice chips from `PendingQuestions`; the choice tap sends `AiInput.Answer`.
- History paging by `Sequence` (`GetMessages(beforeSequence, count)`), compacted messages still shown.
- Session list page support via `IAiSessionManager.Query` (titles, summaries, last activity).
- Optional token / context meter from `UsageChanged`.
- Context warning banner driven by `ContextPressureChanged` (Warning / Critical), with "Compact" and
  "New conversation" actions. This is the reference implementation of D11; it is opt-in on the control.

---

## 8. Phases

### Phase 1 — Core text sessions (done 2026-10-29)
- [x] Spike: streaming + terminating tool (`ask_user`) with the pinned MEAI version; confirm usage reporting per request in streaming. (Both confirmed; see §3, §4.3.)
- [x] Core types: options, inputs, events, documents, `MessagePart`, JSON source-gen context.
- [x] `BroadcastChannel<T>` (hot `IAsyncEnumerable` fan-out, snapshot-first, coalescable vs lifecycle backpressure) + tests.
- [x] `MessageMapper` (MEAI ↔ stored) + round-trip tests.
- [x] `AiSession` / `AiTurn`: streaming, single active turn, cancel-and-replace, interrupt recording.
- [x] ~~`UsageCapturingChatClient`~~ per-request usage read from the stream (spike) + `SessionUsage` / `ContextSnapshot` + estimator + `Pressure` levels and `ContextPressureChanged`.
- [x] Stateless `AiSessionManager` (no caching), `Get` rehydration, `AiSessionSettings` vs runtime split.
- [x] `IAiSessionStore` + `InMemoryAiSessionStore`; write-early semantics; store-allocated `Sequence`; header `Version` check.
- [x] Lazy recovery (`RunId`); two-instance tests (reload on version change, conflict on concurrent turns).
- [x] Interceptors; port `IContextProvider` users. (`DefaultSessionInterceptor` = time prompt + registered tools. Voice selection moves to Phase 3, chat lookup to Phase 2 search.)
- [x] `ask_user` + `AwaitingUser` + `?` fallback.
- [x] Unit tests for L6, L8, L9, L12, P3–P6. (46 tests in `Shiny.AiConversation.Tests`.)

**Phase 1 notes**
- v2 core types are deleted (D2). `Shiny.AiConversation.Maui` and `Shiny.AiConversation.MessageStores.SqliteDocDb`
  are out of `Build.slnf` until Phase 4 / Phase 2 rebuild them; the samples still target the v2 API (Phase 4).
  The Blazor sample deploy workflow triggers on `src/aiconversation/**` for `v*` branches and will fail until then.
- Core no longer references Shiny.Speech / Shiny.Audio. The v2 sound effects (`Resources/*.wav`) were deleted
  with it; restore them into the Voice package in Phase 3 from git (`git show ac44360:src/aiconversation/Shiny.AiConversation/Resources/ok.wav`, etc.).
- Deferred from the contracts to the phase that builds them: `IAiSession.Compact`, `IContextCompactor`,
  `ContextCompacted` (Phase 2); `IAiSessionManager.Memory`, memory events (Phase 2); `ImagePart`,
  `AttachmentRef`, `IAiAttachmentStore`, `AiInput.WithImage` (Phase 4 camera).
- `Audience` on `MessagePart` and the `Silent` tool flag landed in Phase 1 (they were listed in Phase 2).
- Differences from the contracts are listed at the top of [aiconversation-contracts.md](aiconversation-contracts.md).

### Phase 2 — Persistence & context management (done 2026-10-29)
- [x] `DocumentDbAiSessionStore` (SqliteDocDb): two document types, indexes on `SessionId`+`Sequence`, `LastActivity`.
- [x] Rename the store package to `Shiny.AiConversation.Stores.SqliteDocDb` (D2a) and put it back in `Build.slnf`.
- [x] Rolling compaction (`IContextCompactor`, LLM summary, post-turn background run, hard-trim + message-cap fallbacks).
- [x] Title interceptor, long-term memory interceptor.
- [x] `search_conversations` / `read_conversation` (§5.7); spike FTS5 in Shiny.DocumentDb; `ExcludeFromSearch`.
- [x] Memory (§4.8): `IAiMemoryStore` (in-memory + SqliteDocDb), `memory` tool, prompt index, soft limits,
      pre-compaction flush, `IAiMemoryService` for apps, `MemoryChanged` / `MemoryPressureChanged` events.
- [x] `Audience` on `MessagePart`; `Silent` tool flag. (Landed in Phase 1.)
- [x] Spike: Shiny.DocumentDb conditional update / transaction for `Version` + `Sequence`. (§4.6)
- [x] ~~Decide on and build (or skip) the v2 importer.~~ Skipped (D5).

**Phase 2 notes**
- One contract test suite (`StoreContractTests`) runs against both the in-memory and SQLite stores, including
  20 concurrent appends. 91 tests total.
- `SetSqliteDocDbStores(path)` registers both stores; `SetDocDbStores(key)` uses an existing named document
  store, whose options must call `MapAiConversation()`. Storage wrappers (`StoredAiSession`, `StoredAiMessage`,
  `StoredAiSequence`, `StoredAiMemory`) carry the id, version and denormalized query fields.
- The current summary goes into the prompt from the session header; `Summary` messages are history markers for
  the UI and are skipped by the mapper.
- The pre-compaction memory flush is one silent request with only the `memory` tool; nothing from it is persisted
  except memory writes and usage.
- Memory is on by default (`ConfigureMemory(m => m with { Enabled = false })` or `DisabledProfiles` to turn it off).
  The `memory` tool defaults to the global scope for `create` and looks in session → profile → global otherwise.
- Titles are generated in the background after the first exchange, so the next turn never waits; no refresh on
  compaction yet.
- `SQLitePCLRaw.bundle_e_sqlite3` is pinned to 2.1.14 because Shiny.DocumentDb.Sqlite 10.0.2 brings 2.1.11
  (advisory GHSA-2m69-gcr7-jv3q). Drop the pin when DocumentDb is upgraded.

### Phase 3 — Voice (done 2026-10-29)
- [x] `IVoiceConversation` loop: PTT, wake word, follow-up (L4, L5).
- [x] Sentence chunker + markdown stripper (unit tested).
- [x] `InterruptionMode` Off / QuietWords (default) / Full (D15); echo handling (L1); quiet words (L2, L3); interrupt recording.
- [x] Speech engines (§6.1): explicit provider registration, next-sentence TTS prefetch, latency metric.
- [x] Spike: wake-word cost on Android: VAD gate on `IAudioSource` and/or a local `IWakeWordDetector`.
- [x] Sound provider (P10), acknowledgement modes (L10), voice-selection tools ported.

**Phase 3 notes**
- `Shiny.AiConversation.Voice` (net10.0, AOT-compatible): `IVoiceConversation` / `VoiceConversation`,
  `AddShinyAiConversationVoice(v => v.AddVoiceSelectionTools().SetSoundProvider<T>())`. It never registers
  speech engines (D16); the app registers its Shiny.Speech providers.
- One loop, three entry points: wake word (mic open, `WaitingForWakeWord`), push-to-talk (mic closed while
  `Off`), follow-up after `AwaitingUser` (`FollowUp`, times out per L5). `VoiceState` gained `Thinking` and `Paused`.
- Replies stream sentence by sentence (`SentenceChunker` handles decimals, list numbers, abbreviations,
  initials, fenced code). With `CanSynthesizeToStream` + an `IAudioPlayer`, the next sentence is synthesized
  while the current one plays (gapless cloud voices). `ResponseLatency` = end of utterance → first audio.
- Interruption: listening runs while the model thinks *and* speaks. Quiet words act on the first matching
  partial; Full mode barges in on finals with the confidence floor only once audio has started. `Off` mutes
  recognition while speaking plus `EchoSettleWindow` (750 ms), then drains. Every interruption calls
  `turn.Interrupt(spokenText)` with the sentences that fully played. Push-to-talk while speaking stops the
  reply and listens; `StopSpeaking()` just stops.
- Stale results are drained *before* the state is announced (`Listening`/`FollowUp`/`Thinking`), so speech
  that starts the moment the UI says "listening" is never lost; a push-to-talk tap is never dropped as stale.
- On a follow-up timeout the session keeps its `PendingQuestions` (the chat UI can still answer them); only
  the voice loop goes back to the wake word / off. This differs from v2, which cleared them.
- `AiAcknowledgement`: `None` (no sounds, no speech), `AudioBlip` (sounds only), `LessWordy` (speech + a
  per-turn "be concise" instruction via the new `AiInput.Instructions`), `Full`. Non-silent tool calls before
  any text speak `ToolAcknowledgement` ("One moment.").
- Wake-word cost spike result: a local keyword spotter needs a model/library choice (not made). The voice
  driver now has the seam: register an `IWakeWordDetector` and the recognizer stays closed until the wake
  word (fixes cost and privacy). A VAD gate would live in the cloud STT providers (Shiny.Speech.Cloud) and is
  left for later; docs recommend push-to-talk on Android with cloud STT until a detector exists.
- Pause/Resume release the mic and speaker (for the camera tool in Phase 4).

### Phase 4 — MAUI + extras (done 2026-10-29)
- [x] ChatView adapter rewrite (streaming bubble updates, choices, paging, session list).
- [x] `Shiny.AiConversation.Maui.Camera`: `use_camera` (§5.8) with photo / text / barcode / document modes,
      consent gate, `SupportsImagesInToolResults` + follow-up-image fallback, `ImageRetention`, voice pause/resume.
- [x] Memory management view (list / view / edit / delete / forget everything) + optional "Remembered" chip.
- [x] Sample app updates.

**Phase 4 notes**
- `AiChatView` binds `Session` (or resumes the latest session itself and owns it). Bubbles map 1:1 to stored
  user/assistant messages; a streamed reply is one bubble, created on its first text and updated in place
  (`MessageChangeKind.Edited`, throttled to 100 ms). Sent messages come back with the stored message id, so the
  session's own `MessageAdded` merges instead of duplicating. Choice chips send `AiInput.Answer(question, ids)`.
  History pages by sequence. `ShowContextWarnings` / `ContextPressureChanged` / `Compact()` / `NewConversation()`
  are the D11 reference UI. `ConfigureChatView` registers app-wide defaults for unset properties. The session
  list is the sample's job (`IAiSessionManager.Query`); there is no list control. The "Remembered" chip was not built.
- The MAUI package references the Voice package: the mic action is push-to-talk on `IVoiceConversation`,
  attaching it to the chat's session.
- Core gained attachments: `AiInput.WithImage`, `ImagePart` (reference only, never bytes), `IAiAttachmentStore`
  (`InMemoryAiAttachmentStore` default, `FileAiAttachmentStore` via `UseFileAttachments`), `ImageRetention`
  (DropAfterTurn / KeepInContext / DontStore). Session delete cascades attachments.
- Core gained follow-up rounds: `TurnContext.QueueFollowUp(input)` continues the *same* turn with another user
  input (so the voice driver speaks the follow-up's reply). The camera tool uses it when the provider can't
  take images in tool results; with `SupportsImagesInToolResults` the photo goes inline and stored history keeps
  only a `[image/jpeg attachment]` placeholder.
- The `use_camera` engine lives in core (`CameraToolInterceptor`, `ICameraCapture`, `ICameraConsent`, so it is
  unit tested without MAUI); the MAUI package supplies `MediaServiceCameraCapture` over `IMediaService`,
  `AlertCameraConsent`, and `AddAiConversationCamera(c => c.AddScanner(mode, scan))` for analyzer packages.
  Voice is paused through the new core `IAudioFocusParticipant` (the voice conversation registers as one).
- `Microsoft.Maui.Controls` went 10.0.80 → 10.0.100 (required by every `Shiny.Maui.Controls.Camera` with
  `IMediaService`). `SchemaFunction` can return AI content (text + images).
- Samples: both moved to v3 (a `ConversationHost` shares one session between chat and voice). The MAUI sample
  adopts the UIScene lifecycle (`UIApplicationSceneManifest` + `SceneDelegate`): current iOS terminates apps
  without it at launch. Verified on an iPhone 15 Pro.
- Voice fix found while testing: a push-to-talk tap racing the end of a reply could be lost; the handoff is
  now atomic and an unconsumed tap is re-queued.

### Phase 5 — Later
- [ ] Realtime backend (OpenAI Realtime / Gemini Live) behind `IAiSession`.
- [ ] Gapless TTS for native voices (cloud voices get prefetch in Phase 3).
- [ ] Live video frames (realtime backend only).

### Every phase (per CLAUDE.md)
- [x] README.md (Phase 1: root README section + package readme)
- [x] `skills/` — `shiny-aiconversation` skill rewritten for the new API, trigger keywords updated (Phase 1).
- [x] Docs site (`~/Desktop/dev/documentation`): AI conversation pages + release notes (`### 3.0 - TBD`, breaking) (Phase 1;
      voice / chat view / tools pages carry a "2.x, being rebuilt" caution until their phase).

---

## 9. Open decisions

### Decided

| # | Question | Decision |
|---|----------|----------|
| D1 | `ask_user` tool instead of the JSON envelope? | **Yes** (2026-10-21). `AiStructuredOutputMode` / `AiTurnSerializer` are removed. |
| D2 | Package name? | **Keep `Shiny.AiConversation`, complete break** (2026-10-21). No compatibility shims, no `[Obsolete]` bridges; old types are deleted. |
| D3 | Event surface? | **`IAsyncEnumerable` everywhere, Rx is out** (2026-10-21). See §5.3. |

| D13 | Can the assistant search past conversations? | **Yes, opt-in tool** (2026-10-21). `search_conversations` + `read_conversation`; covers compacted and other sessions; text search first, semantic later. See §5.7. |
| D14 | Can the assistant use the camera? | **Yes, opt-in tool** (2026-10-21) in `Shiny.AiConversation.Maui.Camera`, built on Shiny Controls `IMediaService` / CameraView. See §5.8. |
| D15 | Interruption default? | **`InterruptionMode` Off / QuietWords / Full, default `QuietWords`** (2026-10-21). Echo cancellation on for QuietWords and Full. Wake word stays off by default. See §6. |
| D16 | Which speech engines? | **This repo's Shiny.Speech STT/TTS, provider-agnostic** (2026-10-21). The engine is whatever is registered in the Shiny.Speech abstraction; no auto-registration, no engine checks in the voice driver. Docs recommend a cloud provider on Android. See §6.1. |
| D12 | Can the assistant keep notes the user doesn't hear? | **Yes** (2026-10-21). Path-based memory documents in Session / Global / Profile scopes, all in v3.0; silent `memory` tool; user can view, edit and delete everything; soft limits fed back to the model; versioned writes; no binary content. See §4.8. |
| D11 | What happens when the token count gets high? | **The library reports, the UI warns** (2026-10-21). `ContextPressure` Normal/Warning/Critical + event; manual `Compact()`. Auto-compaction kept as a 95% safety net (confirmed 2026-10-21; configurable, can be disabled). See §4.4. |
| D10 | Do sessions end? | **No** (2026-10-21). Token-based like a Claude conversation window: rolling compaction, no `End`, no idle timeout. Removal is `Delete` only. See §4.4, §4.6. |
| D2a | Rename `MessageStores.SqliteDocDb` → `Stores.SqliteDocDb`? | **Yes** (2026-10-29), as part of the break; done when the store is rebuilt in Phase 2. |
| D4 | IDs: `string` or `Guid`? | **`string`, ULID-style and sortable** (2026-10-29). Matches the v2 store and ChatView. |
| D5 | Ship a v2 → v3 history importer? | **No** (2026-10-29). Document a fresh start; revisit only if users ask. |
| D6 | Where does `ContextWindow` come from when the app doesn't set it? | **Optional `IChatClientProvider.ContextWindow`** (2026-10-29); no built-in model table (it goes stale). |
| D7 | Persist system prompts / tool definitions per session? | **No** (2026-10-29): they're rebuilt by interceptors on load. A hash + names may be stored later for debugging. |
| D8 | Encrypt stored conversations at rest? | **Left to the store implementation** (2026-10-29); called out in the docs. |
| D9 | Does the manager keep sessions in memory? | **No** (2026-10-21). Stateless; every `Get` rehydrates from the store. See §4.6. |

### Still open

None for Phase 1.
