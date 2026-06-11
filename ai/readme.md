# Shiny.AiConversation

A centralized AI service library for .NET MAUI apps that orchestrates chat, speech recognition, wake word detection, text-to-speech, and persistent message history into a single `IAiConversationService` interface.

[![NuGet](https://img.shields.io/nuget/v/Shiny.AiConversation.svg)](https://www.nuget.org/packages/Shiny.AiConversation/)

## Features

- **Chat Integration** — Send text or voice messages to any AI backend via [Microsoft.Extensions.AI](https://devblogs.microsoft.com/dotnet/introducing-microsoft-extensions-ai/)
- **Wake Word Detection** — Hands-free activation with continuous keyword listening
- **Speech-to-Text / Text-to-Speech** — Full voice loop powered by [Shiny.Speech](https://github.com/shinyorg/speech)
- **Acknowledgement Modes** — None, AudioBlip (sound effects), LessWordy (concise TTS), or Full (complete TTS)
- **Context Providers** — Pluggable `IContextProvider` visitor pattern for populating an `AiContext` per request. Each provider receives a mutable context containing system prompts, AI tools, quiet words, speech-to-text options, and text-to-speech options. A built-in `ContextProvider` handles time-based prompts, acknowledgement-aware voice prompts, and DI-registered `AITool` instances.
- **Persistent Chat History** — Pluggable `IMessageStore` for storing and querying past conversations
- **AI History Lookup Tool** — Automatically available when an `IMessageStore` is registered, lets the AI search past conversations on its own
- **Voice Selection Tools** — Optional `AddVoiceSelectionTools()` enables the AI to list voices, play samples, and switch its own TTS voice mid-conversation
- **State Management** — Observable `AiState` (Idle / Listening / Thinking / Responding) with events
- **Sound Effects** — Configurable sound stream factories for each state transition
- **Conversation Continuation** — AI responses ending with a question automatically keep the microphone open for a reply
- **Voice Interruption** — Configurable quiet words (e.g., "stop", "cancel") via `AiContext.QuietWords` immediately silence TTS and break out of the conversation. Any other speech during TTS interrupts and continues the conversation with the new utterance.
- **Speech Options** — Configurable `SpeechToTextOptions` and `TextToSpeechOptions` via `AiContext` (culture, silence timeout, voice, speech rate, etc.)

## TODO
- Sessions - ability to start different AI sessions based on time passed
- Acknowledgement sounds is present, but we need acknowledgement "Hi User" or "What can I help you with?"
  - Manually activation could just be a sound?  Maybe only the wake word should have a greeting, and manual text input doesn't need it?
- Speech to text - Wait for anything? then 2 seconds of silence

## Installation

```bash
dotnet add package Shiny.AiConversation
```

## Quick Start

### 1. Register the service

```csharp
using Shiny.AiConversation;

var builder = MauiApp.CreateBuilder();
builder
    .UseMauiApp<App>()
    .ConfigureFonts(fonts => { ... });

// Register an IChatClient in DI (from any Microsoft.Extensions.AI-compatible provider)
builder.Services.AddChatClient(new OpenAIClient("your-api-key").GetChatClient("gpt-4o").AsIChatClient());

builder.Services.AddShinyAiConversation(opts =>
{
    // Optional — enable persistent history (ChatLookupAITool is added automatically)
    opts.SetMessageStore<MyMessageStore>();
});

return builder.Build();
```

### 2. Chat Client Setup

By default, the library resolves `IChatClient` from DI. Simply register your chat client with the container:

```csharp
builder.Services.AddChatClient(new OpenAIClient("your-api-key").GetChatClient("gpt-4o").AsIChatClient());
```

#### Shiny.AiConversation.OpenAi

A ready-made static OpenAI provider. Works with any OpenAI-compatible endpoint (OpenAI, Azure OpenAI, Ollama, etc.).

```bash
dotnet add package Shiny.AiConversation.OpenAi
```

```csharp
builder.Services.AddShinyAiConversation(opts =>
{
    opts.AddStaticOpenAIChatClient(
        apiToken: "your-api-key",
        endpointUri: "https://api.openai.com/v1",
        modelName: "gpt-4o"
    );
});
```

#### Shiny.AiConversation.Maui.GithubCopilot

A MAUI-specific provider that authenticates via the GitHub device code flow and uses the Copilot API. Tokens are stored in `SecureStorage`. Authentication is fully self-contained — the library shows a popup with the device code, copies it to the clipboard, and opens the browser for the user to authorize.

```bash
dotnet add package Shiny.AiConversation.Maui.GithubCopilot
```

```csharp
builder.Services.AddShinyAiConversation(opts =>
{
    opts.AddGithubCopilotChatClient();
});
```

No additional setup is needed — the provider handles the entire OAuth flow, token exchange, caching, and re-authentication on expiry.

#### Custom Provider

For other backends, implement `IChatClientProvider`:

```csharp
using Microsoft.Extensions.AI;
using Shiny.AiConversation;

public class MyChatClientProvider : IChatClientProvider
{
    public async Task<IChatClient> GetChatClient(CancellationToken cancelToken = default)
    {
        // Handle auth, token refresh, etc.
        return BuildChatClient();
    }
}

// Register it:
builder.Services.AddShinyAiConversation(opts =>
{
    opts.SetChatClientProvider<MyChatClientProvider>();
});
```

### 3. Implement `IMessageStore` (optional)

Provide persistent storage for chat history. Without this, `GetChatHistory` and `ClearChatHistory` will throw.

```csharp
using Shiny.AiConversation;

public class MyMessageStore : IMessageStore
{
    public Task Store(string? userTriggeringMessage, ChatResponse response, CancellationToken cancellationToken)
    {
        // Persist the complete AI response
    }

    public Task Clear(DateTimeOffset? beforeDate = null)
    {
        // Clear all or messages before the given date
    }

    public Task<IReadOnlyList<AiChatMessage>> Query(
        string? messageContains = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        // Query with optional filters, return ordered by timestamp
    }
}
```

### 4. Use the service

```csharp
public class ChatViewModel(IAiConversationService aiService) : ObservableObject
{
    public async Task SendMessage(string text)
    {
        await aiService.TalkTo(text, CancellationToken.None);
    }

    public async Task UseMicrophone()
    {
        var access = await aiService.RequestAccess();
        if (access != AccessState.Available)
            return;

        await aiService.ListenAndTalk(CancellationToken.None);
    }

    public async Task StartWakeWord()
    {
        await aiService.StartWakeWord("Hey Assistant");
    }
}
```

## API Overview

### IAiConversationService

| Member | Description |
|--------|-------------|
| `RequestAccess()` | Check speech-to-text access — returns `Available` or `Restricted` |
| `TalkTo(string, CancellationToken)` | Send a text message to the AI |
| `ListenAndTalk(CancellationToken)` | Capture speech via microphone and send to AI |
| `StartWakeWord(string)` | Begin continuous wake word detection |
| `StopWakeWord()` | Stop wake word detection |
| `GetChatHistory(...)` | Query persisted chat history with optional filters |
| `ClearChatHistory(...)` | Clear persisted history (all or before a date) |
| `ClearCurrentChat()` | Clear in-memory session messages |
| `Status` | Current `AiState` (Idle / Listening / Thinking / Responding) |
| `Acknowledgement` | Get/set the response delivery mode |
| `StatusChanged` | Event fired with the new `AiState` on any state change |
| `AiResponded` | Event fired when the AI completes a response with `AiResponse` (Response, WasReadAloud, ExpectsResponse) |

### AiContext

The `AiContext` is a mutable context object populated by `IContextProvider` implementations before each AI request:

| Property | Description |
|----------|-------------|
| `Acknowledgement` | The current acknowledgement mode (set by the service before providers run) |
| `SystemPrompts` | System prompt strings to include in the chat request |
| `Tools` | AI tools available for the request |
| `QuietWords` | Words that stop TTS and break the conversation loop (default: cancel, quiet, shut up, stop, nevermind, never mind, hush) |
| `SpeechToTextOptions` | Options for speech-to-text (culture, silence timeout, prefer on-device) |
| `TextToSpeechOptions` | Options for text-to-speech (culture, voice, speech rate, pitch, volume) |

### Acknowledgement Modes

| Mode | Behavior |
|------|----------|
| `None` | No audio feedback or text-to-speech |
| `AudioBlip` | Short sound effects at state transitions |
| `LessWordy` | Text-to-speech with a "be concise" system prompt |
| `Full` | Text-to-speech with full unmodified responses |

### ChatLookupAITool

When an `IMessageStore` is registered via `SetMessageStore<T>()`, the built-in `ContextProvider` automatically adds a `ChatLookupAITool` (`lookup_chat_history`) to every request. This allows the AI to autonomously search past conversations when the user asks about previous discussions — no extra code required.

### VoiceSelectionTools (optional)

Call `AddVoiceSelectionTools()` during registration to give the AI three voice-related tools:

| Tool | Description |
|------|-------------|
| `get_available_voices` | Lists all available TTS voices, optionally filtered by culture code |
| `play_voice_sample` | Speaks a sample phrase using a specific voice so the user can hear it |
| `change_voice` | Switches the AI's speaking voice; persists for the session |

```csharp
builder.Services.AddShinyAiConversation(opts =>
{
    opts.AddStaticOpenAIChatClient(...);
    opts.AddVoiceSelectionTools();
});
```

Once registered, the user can say things like:
- *"Can you play a few different voices for me?"*
- *"Switch to a different voice"*
- *"Use a British accent"*

The selected voice is written directly to `IAiConversationService.TextToSpeechOptions`, which the service uses for all subsequent TTS responses. Requires `ITextToSpeechService` — available automatically when `AutoAddSpeechServices` is `true` (the default).

## Architecture

```
┌─────────────────────────────────────────────────┐
│                   IAiConversationService                     │
│  (orchestrates chat, speech, sounds, history)    │
├──────────┬──────────────┬───────────────────────┤
│          │              │                       │
│  IChatClientProvider    │    IMessageStore       │
│  (default: resolves    │    (persistence)       │
│   IChatClient from DI) │                        │
│          │              │         │              │
│    IChatClient     ISpeechToText  │   ChatLookupAITool
│  (M.E.AI)          ITextToSpeech │   (optional AITool)
│                    (Shiny.Speech) │              │
│                         │         │              │
│                    IAudioPlayer   │              │
│                   (Shiny.Speech)  │              │
└─────────────────────────────────────────────────┘
```

## Dependencies

| Package | Purpose |
|---------|---------|
| [Microsoft.Extensions.AI](https://www.nuget.org/packages/Microsoft.Extensions.AI) | IChatClient abstraction |
| [Shiny.Speech](https://www.nuget.org/packages/Shiny.Speech) | Speech-to-text, text-to-speech, and audio playback |

## License

MIT
