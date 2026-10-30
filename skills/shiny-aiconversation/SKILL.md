---
name: shiny-aiconversation
description: Generate code for Shiny.AiConversation v3 - session-based AI conversations for .NET over Microsoft.Extensions.AI with persisted, resumable sessions (IAiSessionManager / IAiSession / IAiTurn), streaming replies, the built-in ask_user tool for typed questions and multiple-choice answers, interruption and supersession, usage and context-window pressure tracking, rolling context compaction, silent assistant memory, conversation search, session titles and long-term memory, session interceptors for tools and prompts, and pluggable session/memory stores (in-memory or SQLite via Shiny.AiConversation.Stores.SqliteDocDb)
auto_invoke: true
triggers:
  - shiny ai
  - shiny ai conversation
  - shiny.aiconversation
  - addshinyaiconversation
  - add shiny ai conversation
  - ai session
  - aisession
  - iaisession
  - ai session manager
  - aisessionmanager
  - iaisessionmanager
  - aiinput
  - ai input
  - ai turn
  - aiturn
  - iaiturn
  - aiturnresult
  - aiturnoutcome
  - ask_user
  - ask user tool
  - aiquestion
  - ai question
  - aichoice
  - multiple choice
  - pending questions
  - pendingquestions
  - session interceptor
  - isessioninterceptor
  - sessionloadedcontext
  - turncontext
  - aitoolflags
  - iaisessionstore
  - session store
  - inmemoryaisessionstore
  - aisessiondocument
  - aimessagedocument
  - aisessionsettings
  - context pressure
  - contextpressure
  - context window
  - session usage
  - sessionusage
  - contextsnapshot
  - aiconversationoptions
  - chat client provider
  - ichatclientprovider
  - addstaticopenaichatclient
  - openai static
  - github copilot
  - addgithubcopilotchatclient
  - compaction
  - compact
  - context compaction
  - icontextcompactor
  - assistant memory
  - ai memory
  - iaimemoryservice
  - iaimemorystore
  - memory tool
  - conversation search
  - search_conversations
  - session titles
  - long-term memory
  - sqlite store
  - setsqlitedocdbstores
  - Shiny.AiConversation.Stores.SqliteDocDb
references:
  - registration.md
  - sessions.md
  - questions.md
  - interceptors.md
  - session-store.md
  - compaction.md
  - memory.md
  - built-in-tools.md
  - chat-client-provider.md
---

# Shiny.AiConversation Skill

You are an expert in Shiny.AiConversation v3, a session-based library for talking to an AI through
Microsoft.Extensions.AI. A session is a persisted, never-ending conversation: you start or resume it,
send input, and consume streamed turn events. The core package has no audio dependency.

## Library Overview

**GitHub**: https://github.com/shinyorg/speech (under `src/aiconversation/`)
**NuGet**: `Shiny.AiConversation`
**Namespace**: `Shiny.AiConversation` (registration extensions live in `Shiny`)
**Infrastructure Namespace**: `Shiny.AiConversation.Infrastructure` (implementations)

The library provides:
- **IAiSessionManager**: Stateless factory. `Start`, `Get`, `GetLatest`, `Query`, `Delete`. Every `Get` reads the store and returns a **new** instance that the caller owns and disposes. See `sessions.md`
- **IAiSession**: One conversation. `Send(AiInput)` returns an `IAiTurn`; `GetEvents()` is a hot event stream whose first item is a `Snapshot`; exposes `State`, `PendingQuestions`, `Usage`, `Context`, `ActiveTurn`, `GetMessages` (history paging), `SetTitle`, `UpdateSettings`. Sessions never end; removal is `IAiSessionManager.Delete`
- **IAiTurn**: One request/response exchange including tool calls. `Events` (replayable), `Completion` (never faults), `Interrupt(spokenText)`
- **ask_user**: Built-in terminating tool the model calls when it needs an answer. It drives `PendingQuestions` and the `AwaitingUser` state. See `questions.md`
- **ISessionInterceptor**: Adds tools and system prompts per session (`OnSessionLoaded`) or per turn (`OnBeforeTurn`) and reacts after a turn (`OnAfterTurn`). Tools are never persisted. See `interceptors.md`
- **IAiSessionStore**: Persists session headers and append-only messages. `InMemoryAiSessionStore` is the default; `opts.SetSqliteDocDbStores(path)` (NuGet `Shiny.AiConversation.Stores.SqliteDocDb`) persists to SQLite. See `session-store.md`
- **Compaction**: `session.Compact()` folds old messages into a rolling summary; runs automatically at 95% of the context window. See `compaction.md`
- **Assistant memory**: a silent `memory` tool (on by default) plus `IAiSessionManager.Memory` (`IAiMemoryService`) so the user can see/edit/delete notes. See `memory.md`
- **Built-in tools/interceptors**: `AddConversationSearch()`, `AddSessionTitles()`, `AddLongTermMemory()`, `AddTool(tool, flags, profiles)`. See `built-in-tools.md`
- **IChatClientProvider**: Supplies the `IChatClient`; optional `ContextWindow`. See `chat-client-provider.md`

**Provider packages**:
- **Shiny.AiConversation.OpenAi**: `opts.AddStaticOpenAIChatClient(apiToken, endpointUri, modelName)`
- **Shiny.AiConversation.GithubCopilot**: `opts.AddStaticGithubCopilotChatClient(personalAccessToken, modelName)`
- **Shiny.AiConversation.Maui.GithubCopilot**: `opts.AddGithubCopilotChatClient()` (device-code OAuth, SecureStorage)

**Available now:** sessions, the SQLite store (`Shiny.AiConversation.Stores.SqliteDocDb`), compaction,
assistant memory, conversation search, titles and long-term memory.
**Not available in this version (being rebuilt in later v3 phases):** voice (wake word, push-to-talk,
text-to-speech, acknowledgements, quiet words, voice-selection tools) and the MAUI chat adapter
(`Shiny.AiConversation.Maui`). The old `Shiny.AiConversation.MessageStores.SqliteDocDb` package is replaced by
`Shiny.AiConversation.Stores.SqliteDocDb`. The v2 API
(`IAiConversationService`, `TalkTo`, `ListenAndTalk`, `IMessageStore`, `IContextProvider`, `AiTurn`
JSON envelope, `AiStructuredOutputMode`) has been **removed** - never generate code against it.

## Dependencies

- `Microsoft.Extensions.AI` - `IChatClient`, `AITool`, `AIFunctionFactory`, `ChatMessage`

## When to Use This Skill

Invoke this skill when the user wants to:
- Register Shiny.AiConversation and choose a chat client provider
- Start, resume ("continue where I left off"), list or delete AI conversations
- Send text and stream the reply into a UI
- Show the AI's questions as choice buttons and send the selected answer
- Stop or replace a reply that is still generating
- Show token usage or warn that a conversation is getting long
- Add tools or system prompts to sessions, optionally per profile
- Implement a custom session store, or persist sessions to SQLite
- Compact long conversations, or let the assistant remember things across sessions
- Let the assistant search past conversations, title sessions automatically

## Code Generation Instructions

### 1. Registration

```csharp
using Shiny;

builder.Services.AddChatClient(new OpenAIClient("key").GetChatClient("gpt-4o").AsIChatClient());
builder.Services.AddShinyAiConversation(opts =>
{
    opts.AddInterceptor<AssistantInterceptor>();
    opts.AddTool(AIFunctionFactory.Create(GetWeather, "get_weather"));
    opts.AddTool(AIFunctionFactory.Create(FindRecipe, "find_recipe"), AiToolFlags.None, "cooking");
});
```

See `registration.md` for every option and what is registered automatically.

### 2. Start or resume a session, then send

```csharp
public class ChatViewModel(IAiSessionManager manager) : IAsyncDisposable
{
    IAiSession? session;

    public async Task Open()
        => this.session = await manager.GetLatest() ?? await manager.Start();

    public async Task Send(string text)
    {
        var turn = this.session!.Send(AiInput.Text(text));
        await foreach (var e in turn.Events)
        {
            if (e is TextDelta delta)
                this.AppendToReply(delta.Text);
        }

        var result = await turn.Completion;
        if (result.Outcome == AiTurnOutcome.Failed)
            this.ShowError(result.Error!);
    }

    public async ValueTask DisposeAsync()
    {
        if (this.session != null)
            await this.session.DisposeAsync();
    }
}
```

### 3. Bind a UI to session events

Run one `await foreach` over `GetEvents` on a background task and marshal to the UI thread. The first
event is always a `Snapshot`, so you never read properties and subscribe separately.

```csharp
_ = Task.Run(async () =>
{
    await foreach (var e in session.GetEvents(cancelToken))
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (e)
            {
                case Snapshot s:          this.Apply(s.State, s.PendingQuestions); break;
                case MessageAdded m:      this.AddBubble(m.Message); break;
                case MessageUpdated m:    this.ReplaceBubble(m.Message); break;
                case StateChanged s:      this.IsBusy = s.State is AiSessionState.Thinking or AiSessionState.Responding; break;
                case TurnEventOccurred { Event: TextDelta d }: this.AppendStreaming(d.Text); break;
                case ContextPressureChanged c when c.Context.Pressure == ContextPressure.Warning:
                    this.ShowBanner("This conversation is getting long"); break;
            }
        });
    }
});
```

### 4. Questions

```csharp
foreach (var q in session.PendingQuestions.Where(x => x.HasChoices))
    ShowChoices(q.Text, q.Choices!, choice => session.Send(AiInput.Answer(q, choice.Id)));
```

See `questions.md`.

## Rules / Gotchas

1. **The manager is stateless.** `Get` / `GetLatest` return a new instance every call; the caller disposes it (`await using`). Disposing releases the instance, never the stored session.
2. **Share one instance** between pages/components that work on the same session. Two instances of one session work (each reloads when the other wrote), but two *concurrent* turns on different instances make the later header save fail with `SessionConflictException` (reported as `AiTurnOutcome.Failed`).
3. **`Send` supersedes.** A session has at most one active turn; sending while a turn runs cancels it with `AiTurnOutcome.Superseded`. Don't queue sends yourself.
4. **`Completion` never faults.** Failures come back as `AiTurnOutcome.Failed` with `Result.Error`; cancellation and interruption are `Interrupted` / `Superseded`, not exceptions. Don't wrap `await turn.Completion` in try/catch for model errors.
5. **Tools are never persisted.** Add them via `opts.AddTool` or an `ISessionInterceptor` (which runs on `Start` *and* every `Get`). There is no `Start(tools)` overload. Use `AiSessionSettings.Profile` to vary tools/prompts per session.
6. **Don't implement questions yourself.** No JSON reply formats, no prompt telling the model to output JSON; the built-in `ask_user` tool handles it and a reply ending in `?` is the fallback.
7. **The core has no audio.** Don't reference Shiny.Speech / wake word / TTS APIs from this package; voice returns in a later phase.
8. **Use the store's documents for history,** never raw `ChatMessage`/`ChatResponse` objects. `session.GetMessages(beforeSequence, count)` pages newest-first by `Sequence`.
9. **Context vs. usage:** `session.Usage` is cumulative billing; `session.Context` is how full the model's window is. `Pressure` is null unless a context window is known (`AiSessionSettings.ContextWindow` or `IChatClientProvider.ContextWindow`). The library reports; the app decides what to show.
10. **AOT:** serialize documents with `AiConversationJsonContext` (source-generated), never reflection-based `JsonSerializer` calls.
11. **Memory is ON by default.** Every session gets the silent `memory` tool and a `<memory_index>` prompt. Turn it off with `opts.ConfigureMemory(m => m with { Enabled = false })` or per profile with `DisabledProfiles = ["private"]`. Tell users the assistant can save notes, and give them `manager.Memory` to view/edit/clear them.
12. **Memory tool calls are ModelOnly and Silent.** They're stored as `ToolCallPart`/`ToolResultPart` with `Audience.ModelOnly`, never spoken or shown; surface them only through `MemoryChanged` (e.g. a "Remembered" chip).
13. **Compaction runs on the turn queue.** `Compact()` waits for the active turn; auto-compaction runs after a turn completes (in the background) and before a turn that's over the threshold. Never compact from inside an interceptor turn hook.
14. **`MessageRole.Summary` messages are UI markers.** The current summary is sent to the model from the session header; don't feed summary messages back yourself. Compacted messages stay in the store (`Compacted = true`) for history and search.
15. **`SetDocDbStores(key)` needs `MapAiConversation()`** on that document store's options (version mapping, full-text index, JSON metadata). `SetSqliteDocDbStores(path)` does it for you.
