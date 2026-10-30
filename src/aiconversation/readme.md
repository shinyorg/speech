# Shiny.AiConversation

Session-based AI conversations for .NET apps on top of [Microsoft.Extensions.AI](https://devblogs.microsoft.com/dotnet/introducing-microsoft-extensions-ai/):
streamed replies, typed questions, persisted and resumable sessions, and token/context tracking.

[![NuGet](https://img.shields.io/nuget/v/Shiny.AiConversation.svg)](https://www.nuget.org/packages/Shiny.AiConversation/)

> **v3 is a rewrite in progress.** The core text-session API below is done. The voice loop (wake word,
> text-to-speech, interruption, acknowledgements), the MAUI chat adapter and the SQLite session store are
> being rebuilt on top of sessions and are not in this version yet. The core package no longer depends on
> Shiny.Speech or Shiny.Audio.

## Features

- **Sessions** — `IAiSessionManager` starts, resumes (`Get` / `GetLatest`), queries and deletes sessions. Sessions never end; they're persisted and can be picked up again at any time
- **Streaming turns** — `session.Send(...)` returns an `IAiTurn` whose events stream text as it arrives; a new `Send` supersedes the active turn
- **Typed questions** — the model calls the built-in `ask_user` tool when it needs an answer; the session moves to `AwaitingUser` with `PendingQuestions` (optionally with multiple-choice `AiChoice`s). A reply that ends in `?` still keeps the conversation open
- **Interruption** — `turn.Interrupt(spokenText)` records what the user actually heard, and the model is told the reply was cut off
- **Usage & context** — cumulative `SessionUsage` for billing and a `ContextSnapshot` for how full the context window is, with `Normal` / `Warning` / `Critical` pressure for the UI to act on
- **Interceptors** — `ISessionInterceptor` adds tools and system prompts per session (`OnSessionLoaded`) or per turn (`OnBeforeTurn`), optionally by `Profile`
- **Honest persistence** — the user message is stored before the model is called and the reply is stored as streaming then finalized, so failures and crashes leave an accurate record. Tool calls are persisted too
- **One event stream** — `session.GetEvents()` is an `IAsyncEnumerable` that starts with a `Snapshot`, so a UI binds to one ordered stream

## Quick Start

```csharp
// Register an IChatClient in DI (from any Microsoft.Extensions.AI-compatible provider)
builder.Services.AddChatClient(new OpenAIClient("your-api-key").GetChatClient("gpt-4o").AsIChatClient());

builder.Services.AddShinyAiConversation(opts =>
{
    opts.AddInterceptor<MyInterceptor>();                       // optional: tools + prompts
    opts.AddTool(AIFunctionFactory.Create(GetWeather), AiToolFlags.None);
    // opts.SetSessionStore<MyStore>();                         // default: in-memory
});
```

### Chat client providers

```csharp
// Ready-made static OpenAI-compatible provider (Shiny.AiConversation.OpenAi)
opts.AddStaticOpenAIChatClient(apiToken: "your-api-key", endpointUri: "https://api.openai.com/v1", modelName: "gpt-4o");

// MAUI GitHub Copilot — self-contained device-code OAuth, tokens in SecureStorage (Shiny.AiConversation.Maui.GithubCopilot)
opts.AddGithubCopilotChatClient();
```

For other backends, implement `IChatClientProvider` and register with `opts.SetChatClientProvider<MyProvider>()`.
Set `ContextWindow` on the provider (or `AiSessionSettings.ContextWindow`) to get context pressure.

### Talk to the AI

```csharp
public class ChatViewModel(IAiSessionManager sessions)
{
    IAiSession? session;

    public async Task Send(string text)
    {
        this.session ??= await sessions.GetLatest() ?? await sessions.Start();

        var turn = this.session.Send(AiInput.Text(text));
        await foreach (var e in turn.Events)
        {
            if (e is TextDelta delta)
                AppendToBubble(delta.Text);
        }

        var result = await turn.Completion;   // never throws; check result.Outcome
        if (result.Outcome == AiTurnOutcome.AwaitingUser)
            ShowChoices(this.session.PendingQuestions);
    }

    // a tapped choice goes back as a normal message
    public void Answer(AiQuestion question, AiChoice choice)
        => this.session!.Send(AiInput.Answer(question, choice.Id));
}
```

A UI that wants everything (both sides of the conversation, state, usage, errors) subscribes once:

```csharp
await foreach (var e in session.GetEvents(ct))   // first item is always a Snapshot
{
    switch (e)
    {
        case MessageAdded m: ...; break;
        case TurnEventOccurred { Event: TextDelta d }: ...; break;
        case StateChanged s: ...; break;
        case ContextPressureChanged p: ...; break;  // e.g. "this conversation is getting long"
    }
}
```

## API Overview

| Type | Description |
|------|-------------|
| `IAiSessionManager` | `Start(settings)`, `Get(id)`, `GetLatest()`, `Query(query)`, `Delete(id)`. Stateless: every load returns a new instance that the caller disposes |
| `IAiSession` | `Send(input)`, `GetEvents()`, `GetMessages(beforeSequence, count)`, `SetTitle`, `UpdateSettings`; `State`, `PendingQuestions`, `Usage`, `Context`, `ActiveTurn` |
| `IAiTurn` | `Events` (replayable), `Completion` (`AiTurnResult` with `Outcome`: Completed / AwaitingUser / Interrupted / Superseded / Failed), `Interrupt(spokenText)` |
| `AiInput` | `Text(...)`, `Voice(...)` (asks for spoken-style replies and at most one question), `Answer(question, choiceIds)` |
| `AiSessionSettings` | Persisted per session: `Profile`, `ContextWindow`, warning/critical thresholds, `Tags`, `Metadata`, `ExcludeFromSearch` |
| `ISessionInterceptor` | `OnSessionLoaded` (runs on Start **and** every Get), `OnBeforeTurn`, `OnAfterTurn` |
| `IAiSessionStore` | Session headers + append-only messages. `InMemoryAiSessionStore` is the default; serialize with `AiConversationJsonContext` |
| `IChatClientProvider` | Supplies the `IChatClient`; optional `ContextWindow`. The session adds function invocation itself when the client lacks it |

## Dependencies

| Package | Purpose |
|---------|---------|
| [Microsoft.Extensions.AI](https://www.nuget.org/packages/Microsoft.Extensions.AI) | `IChatClient` abstraction and the tool loop |

## Documentation

Full documentation: https://shinylib.net/aiconversation/

