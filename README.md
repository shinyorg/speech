# Shiny.Speech &amp; Shiny.AiConversation

This repository is the home for two complementary library families:

- **Shiny.Speech** — Cross-platform speech services for .NET MAUI and Blazor WebAssembly: speech-to-text and text-to-speech with pluggable cloud providers. Audio capture and playback are provided by the standalone **Shiny.Audio** package (referenced automatically).
- **Shiny.Audio** — Cross-platform microphone capture (`IAudioSource`) and stream playback (`IAudioPlayer`) with VU-level metering. Usable on its own; also the audio backbone for Shiny.Speech.
- **Shiny.AiConversation** — A centralized AI service that orchestrates chat, speech recognition, wake word detection, text-to-speech, and persistent message history into a single `IAiConversationService`. AiConversation drives much of the real-world feature set (and bug surface) of the speech stack, which is why both live and ship from here together.

All packages share a single version, defined by `version.json` at the repo root (Nerdbank.GitVersioning).

## Libraries

| Package | Description | Targets |
|---------|-------------|---------|
| **Shiny.Audio** | Standalone audio capture (`IAudioSource`) + playback (`IAudioPlayer`) with native platform implementations | net10.0-ios, net10.0-android, net10.0-windows, net10.0 (Browser/WASM) |
| **Shiny.Speech** | Core STT/TTS interfaces + native platform implementations (references Shiny.Audio for capture/playback) | net10.0-ios, net10.0-android, net10.0-windows, net10.0 (Browser/WASM) |
| **Shiny.Speech.Cloud** | Cloud provider abstractions + `CloudSpeechToText` / `CloudTextToSpeech` implementations | net10.0 |
| **Shiny.Speech.Azure** | Azure AI Speech provider (STT + TTS) | net10.0 |
| **Shiny.Speech.ElevenLabs** | ElevenLabs provider (STT + TTS) | net10.0 |
| **Shiny.Speech.Typecast** | Typecast provider (TTS only, via the `typecast-csharp` SDK) | net10.0 |
| **Shiny.AiConversation** | Central `IAiConversationService` orchestrating chat + the full voice loop | net10.0 (+ MAUI platforms) |
| **Shiny.AiConversation.OpenAi** | Ready-made static OpenAI-compatible chat client provider | net10.0 |
| **Shiny.AiConversation.Maui.GithubCopilot** | MAUI GitHub Copilot provider (device-code OAuth, SecureStorage) | net10.0 (MAUI) |
| **Shiny.AiConversation.MessageStores.SqliteDocDb** | SQLite/DocumentDb-backed `IMessageStore` for persistent chat history | net10.0 |

## Getting Started

### Native Platform Speech

Use the built-in OS speech engines — no cloud account needed. Works on MAUI (iOS, Android, Windows) and Blazor WebAssembly (via Web Speech API).

```csharp
builder.Services.AddSpeechServices();
// Registers: ISpeechToTextService, ITextToSpeechService, IAudioSource, IAudioPlayer
// On Browser/WASM: auto-detected via OperatingSystem.IsBrowser()
```

> **Audio types moved to `Shiny.Audio`.** `IAudioSource`, `IAudioPlayer`, `PipeStream`, and
> `AccessState` now live in the `Shiny.Audio` namespace (they used to be in `Shiny.Speech`). Add
> `using Shiny.Audio;` where you consume them. `AddSpeechServices()` still wires them up; to register
> audio on its own (without speech) call `builder.Services.AddAudioServices();`.

### Azure AI Speech (Cloud)

```csharp
builder.Services.AddAzureSpeech("your-subscription-key", "your-region");
```

### ElevenLabs (Cloud)

```csharp
// Register both STT (Scribe) and TTS:
builder.Services.AddElevenLabsSpeech("your-api-key");

// Or pick one:
builder.Services.AddElevenLabsSpeechToText("your-api-key");
builder.Services.AddElevenLabsTextToSpeech("your-api-key");
```

### Typecast (Cloud, TTS only)

```csharp
builder.Services.AddTypecastSpeech("your-typecast-api-key");

// Or configure the model / default voice / audio format:
builder.Services.AddTypecastSpeech(new TypecastConfig
{
    ApiKey = "your-typecast-api-key",
    DefaultVoiceId = "<voice-id>",   // call ITextToSpeechService.GetVoicesAsync() to discover ids
    Model = TTSModel.SsfmV30
});
```

> Typecast has no fixed public default voice — set `DefaultVoiceId` (or pass a `VoiceInfo` per call via
> `TextToSpeechOptions.Voice`). Use `GetVoicesAsync()` to list the voice ids available to your account.

### Changing credentials at runtime

You configure providers exactly as above — but the config objects are **mutable singletons**, so you can
change the API key (or region / model / voice) at any time and the provider picks it up on its next call.
No re-registration required. Hold your config instance, or resolve it from DI:

```csharp
var config = new AzureSpeechConfig { SubscriptionKey = "initial-key", Region = "eastus" };
builder.Services.AddAzureSpeech(config);

// ...later, e.g. after the user pastes a new key in settings:
config.SubscriptionKey = "rotated-key";        // next Speak/recognize uses it

// Or resolve it from the container if you didn't keep a reference:
serviceProvider.GetRequiredService<TypecastConfig>().ApiKey = "new-key";
```

Providers that cache an SDK/HTTP client (ElevenLabs, Typecast) transparently rebuild it when the key
changes; Azure and OpenAI read the config on every call.

## Usage

### Text-to-Speech

```csharp
public class MyService(ITextToSpeechService tts)
{
    public async Task SpeakAsync()
    {
        await tts.SpeakAsync("Hello world!", new TextToSpeechOptions
        {
            SpeechRate = 1.2f,
            Pitch = 1.0f,
            Volume = 0.8f
        });
    }
}
```

### Playing Audio (`IAudioPlayer`)

`IAudioPlayer` plays a `Stream`, or — via `PlayAsync(string)` — a **remote URL or a local file path**.
You pass a plain URL/path; each platform resolves it natively, so you never build a platform-specific
file URI:

```csharp
public class Player(IAudioPlayer audioPlayer)
{
    public Task PlayRemote() => audioPlayer.PlayAsync("https://example.com/clip.mp3");
    public Task PlayLocal()  => audioPlayer.PlayAsync(Path.Combine(FileSystem.AppDataDirectory, "chime.mp3"));
    public Task PlayStream(Stream mp3) => audioPlayer.PlayAsync(mp3);
}
```

An absolute `http`/`https` value is treated as a remote source (progressively streamed on Android,
Windows, and Browser; buffered on Apple); anything else is treated as a local file path. In the
browser, a local path means an app-relative URL (there is no device file system).

### VU Meter (Audio Level)

`ITextToSpeechService` and `IAudioPlayer` expose an `AudioLevelChanged` event that fires periodically while audio is playing with a normalized `0.0`–`1.0` RMS level. Check `IsPlayerAnalysisSupported` before binding UI.

```csharp
if (tts.IsPlayerAnalysisSupported)
    tts.AudioLevelChanged += (s, level) =>
        MainThread.BeginInvokeOnMainThread(() => MyVuBar.Progress = level);
```

| Surface | iOS / macOS | Android | Windows | Browser |
|---|---|---|---|---|
| Native `ITextToSpeechService` | ✅ | ✅ | ❌ | ❌ |
| Cloud `ITextToSpeechService` (Azure / OpenAI / ElevenLabs / custom) | ✅ | ✅ | ❌ | ❌ |
| `IAudioPlayer` (generic playback) | ✅ | ✅ | ❌ | ❌ |

On Apple platforms, native TTS routes `AVSpeechSynthesizer` through `AVAudioEngine` + `AVAudioPlayerNode` so audio levels can be tapped. The engine is created lazily on first speak and kept warm across utterances — only the first utterance pays ~50–150 ms additional startup.

### Speech-to-Text

The `ISpeechToTextService` uses a Start/Stop model with events, allowing multiple consumers to observe recognition results simultaneously.

```csharp
public class MyService(ISpeechToTextService stt) : IDisposable
{
    public async Task StartListeningAsync()
    {
        var access = await stt.RequestAccess();
        if (access != AccessState.Available)
            return;

        // Subscribe to events
        stt.ResultReceived += OnResult;
        stt.KeywordHeard += OnKeyword;
        stt.Error += OnError;

        // Start listening (throws if already listening)
        await stt.Start(new SpeechRecognitionOptions
        {
            Culture = CultureInfo.GetCultureInfo("en-US"),
            SilenceTimeout = TimeSpan.FromSeconds(3),
            Keywords = ["Yes", "No", "Maybe"]
        });
    }

    public async Task StopListeningAsync()
    {
        await stt.Stop(); // no-op if not listening
        stt.ResultReceived -= OnResult;
        stt.KeywordHeard -= OnKeyword;
        stt.Error -= OnError;
    }

    void OnResult(object? sender, SpeechRecognitionResult result)
        => Console.WriteLine($"[{(result.IsFinal ? "FINAL" : "partial")}] {result.Text}");

    void OnKeyword(object? sender, string keyword)
        => Console.WriteLine($"Keyword detected: {keyword}");

    void OnError(object? sender, SpeechRecognitionError error)
        => Console.WriteLine($"Error: {error.Message}");

    public void Dispose() => StopListeningAsync().GetAwaiter().GetResult();
}
```

### Voice Processing (Noise Suppression & Echo Cancellation)

Microphone capture can request platform voice-processing effects to strip background noise and,
critically, to **cancel your text-to-speech output from the mic** so it isn't re-captured while the
mic is open (barge-in). Configure it via `AudioProcessingOptions` — either directly on
`IAudioSource.StartCaptureAsync(...)` or through `SpeechRecognitionOptions.AudioProcessing` (honored
by the cloud providers, which capture through `IAudioSource`):

```csharp
await stt.Start(new SpeechRecognitionOptions
{
    Culture = CultureInfo.GetCultureInfo("en-US"),
    AudioProcessing = AudioProcessingOptions.VoiceChat   // AEC + noise suppression + AGC
});

// or, capturing raw audio directly:
var stream = await audioSource.StartCaptureAsync(new AudioProcessingOptions
{
    EchoCancellation = true,      // subtracts speaker/TTS output from the mic signal
    NoiseSuppression = true,      // attenuates steady background noise
    AutomaticGainControl = true   // normalizes capture level
});
```

Each flag is **best-effort** and maps to native voice processing:

| Effect | iOS / macOS | Android | Windows | Browser |
|---|---|---|---|---|
| Echo Cancellation | ✅ Voice-Processing I/O | ✅ `AcousticEchoCanceler` + VoiceCommunication | ⚠️ best-effort (Communications pipeline) | ✅ WebRTC AEC3 |
| Noise Suppression | ✅ (bundled) | ✅ `NoiseSuppressor` | ⚠️ best-effort | ✅ |
| Automatic Gain Control | ✅ (bundled) | ✅ `AutomaticGainControl` | ⚠️ best-effort | ✅ |

Notes:
- **Apple** bundles all three into a single Voice-Processing I/O unit — enabling any flag enables the whole chain (they can't be toggled independently).
- **Android** effect availability is device/driver dependent; unavailable effects are skipped. Requesting echo cancellation also routes capture through `VoiceCommunication`.
- **Windows** `AudioGraph` exposes no per-effect control; requesting any effect selects the `Communications` capture category, which engages driver-provided AEC/NS when present.
- These are **OS/hardware** cancellers referencing the real speaker feed, so any device audio (not just library-played TTS) is cancelled.
- **Native on-device** `ISpeechToTextService` implementations manage their own microphone and are unaffected by this setting — it applies to `IAudioSource` capture (cloud providers, raw capture).

### Extension Methods (Convenience)

```csharp
// Simple: wait for silence (starts and stops automatically)
var text = await stt.ListenUntilSilence(cancellationToken: ct);

// Wake word: "Hey Computer, do something" → returns "do something"
var command = await stt.StatementAfterKeyword(["Hey Computer"], cancellationToken: ct);

// Wait for a specific keyword (with optional timeout)
var answer = await stt.WaitListenForKeywords(["Yes", "No"], timeout: TimeSpan.FromSeconds(30), cancellationToken: ct);

// Continuous keyword stream
await foreach (var keyword in stt.ListenForKeywords(["Up", "Down", "Left", "Right"], cancellationToken: ct))
{
    Console.WriteLine($"Direction: {keyword}");
}
```

## Custom Cloud Provider

Implement `ISpeechToTextProvider` and/or `ITextToSpeechProvider` from `Shiny.Speech.Cloud`:

```csharp
public class MyCloudSttProvider : ISpeechToTextProvider
{
    public event EventHandler<SpeechRecognitionError>? Error;

    public async IAsyncEnumerable<SpeechRecognitionResult> RecognizeAsync(
        Stream audioStream,
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Read PCM audio from audioStream (16kHz, 16-bit, mono)
        // Yield recognition results...

        // For continuous recognition, surface non-fatal errors (e.g. a transient
        // network blip between chunked requests) without aborting the session:
        // Error?.Invoke(this, new SpeechRecognitionError("network blip", ex));
    }
}

// Register:
builder.Services.AddCloudSpeechToText<MyCloudSttProvider>();
```

`CloudSpeechToText` subscribes to the provider's `Error` event and forwards it to the service-level `ISpeechToTextService.Error`, so app code only needs to wire one handler.

## Platform Requirements

| Platform | STT | TTS | Audio Capture | Audio Playback |
|----------|-----|-----|---------------|----------------|
| iOS 15+ (incl. CarPlay) | SFSpeechRecognizer | AVSpeechSynthesizer | AVAudioEngine | AVAudioPlayer |
| Android 26+ | SpeechRecognizer | Android TTS | AudioRecord | MediaPlayer |
| Windows 10 19041+ | Windows.Media.SpeechRecognition | Windows.Media.SpeechSynthesis | AudioGraph | MediaPlayer |
| Browser (WASM) | Web Speech API (`SpeechRecognition`) | Web Speech API (`SpeechSynthesis`) | Web Audio API (`getUserMedia` + `ScriptProcessorNode`) | HTML5 `Audio` |

### Browser (Blazor WebAssembly)

No manifest changes and **no `<script>` tag** needed — the browser prompts the user for microphone access automatically, and the JS interop module ships **inside the `Shiny.Audio` package** as a static web asset (`_content/Shiny.Audio/shiny-audio.js`). It is loaded on demand via `JSHost.ImportAsync`, so referencing `Shiny.Speech` (or `Shiny.Audio` directly) is all that's required.

> **Note:** `IAudioSource` captures raw PCM audio in the browser using the Web Audio API (`getUserMedia` + `ScriptProcessorNode`), downsampled to 16kHz 16-bit mono. Audio playback (`IAudioPlayer`) accepts any browser-supported format via a base64 data URL.

### iOS/macOS

Add to `Info.plist`:
```xml
<key>NSSpeechRecognitionUsageDescription</key>
<string>Speech recognition description</string>
<key>NSMicrophoneUsageDescription</key>
<string>Microphone description</string>
```

### Android

Add to `AndroidManifest.xml`:
```xml
<uses-permission android:name="android.permission.RECORD_AUDIO" />
<uses-permission android:name="android.permission.MODIFY_AUDIO_SETTINGS" />
```
`MODIFY_AUDIO_SETTINGS` is required for the TTS audio-level Visualizer and for the native STT beep suppression.

---

# Shiny.AiConversation

A centralized AI service library for .NET MAUI apps that orchestrates chat, speech recognition, wake word detection, text-to-speech, and persistent message history into a single `IAiConversationService` interface. It builds directly on Shiny.Speech for the voice loop.

[![NuGet](https://img.shields.io/nuget/v/Shiny.AiConversation.svg)](https://www.nuget.org/packages/Shiny.AiConversation/)

> The package-level readme lives at [`src/aiconversation/readme.md`](src/aiconversation/readme.md).

## Features

- **Chat Integration** — Send text or voice messages to any AI backend via [Microsoft.Extensions.AI](https://devblogs.microsoft.com/dotnet/introducing-microsoft-extensions-ai/)
- **Wake Word Detection** — Hands-free activation with continuous keyword listening
- **Speech-to-Text / Text-to-Speech** — Full voice loop powered by Shiny.Speech (above)
- **Acknowledgement Modes** — None, AudioBlip (sound effects), LessWordy (concise TTS), or Full (complete TTS)
- **Context Providers** — Pluggable `IContextProvider` visitor pattern for populating an `AiContext` per request (system prompts, AI tools, quiet words, speech options)
- **Persistent Chat History** — Pluggable `IMessageStore` for storing and querying past conversations
- **AI History Lookup Tool** — Automatically available when an `IMessageStore` is registered, lets the AI search past conversations on its own
- **Voice Selection Tools** — Optional `AddVoiceSelectionTools()` lets the AI list voices, play samples, and switch its own TTS voice mid-conversation
- **State Management** — Observable `AiState` (Idle / Listening / Thinking / Responding) with events
- **Sound Effects** — Configurable sound stream factories for each state transition
- **Conversation Continuation** — AI responses ending with a question automatically keep the microphone open for a reply
- **Voice Interruption** — Configurable quiet words (e.g., "stop", "cancel") via `AiContext.QuietWords` immediately silence TTS and break the loop; any other speech during TTS interrupts and continues with the new utterance

## Quick Start

```csharp
using Shiny.AiConversation;

var builder = MauiApp.CreateBuilder();
builder.UseMauiApp<App>();

// Register an IChatClient in DI (from any Microsoft.Extensions.AI-compatible provider)
builder.Services.AddChatClient(new OpenAIClient("your-api-key").GetChatClient("gpt-4o").AsIChatClient());

builder.Services.AddShinyAiConversation(opts =>
{
    // Optional — enable persistent history (ChatLookupAITool is added automatically)
    opts.SetMessageStore<MyMessageStore>();
});

return builder.Build();
```

### Chat client providers

```csharp
// Ready-made static OpenAI-compatible provider (Shiny.AiConversation.OpenAi)
opts.AddStaticOpenAIChatClient(apiToken: "your-api-key", endpointUri: "https://api.openai.com/v1", modelName: "gpt-4o");

// MAUI GitHub Copilot — self-contained device-code OAuth, tokens in SecureStorage (Shiny.AiConversation.Maui.GithubCopilot)
opts.AddGithubCopilotChatClient();
```

For other backends, implement `IChatClientProvider` and register with `opts.SetChatClientProvider<MyProvider>()`.

### Use the service

```csharp
public class ChatViewModel(IAiConversationService aiService)
{
    public Task SendMessage(string text) => aiService.TalkTo(text, CancellationToken.None);

    public async Task UseMicrophone()
    {
        if (await aiService.RequestAccess() == AccessState.Available)
            await aiService.ListenAndTalk(CancellationToken.None);
    }

    public Task StartWakeWord() => aiService.StartWakeWord("Hey Assistant");
}
```

## API Overview

### IAiConversationService

| Member | Description |
|--------|-------------|
| `RequestAccess()` | Check speech-to-text access — returns `Available` or `Restricted` |
| `TalkTo(string, CancellationToken)` | Send a text message to the AI |
| `ListenAndTalk(CancellationToken)` | Capture speech via microphone and send to AI |
| `StartWakeWord(string)` / `StopWakeWord()` | Begin / stop continuous wake word detection |
| `GetChatHistory(...)` / `ClearChatHistory(...)` | Query / clear persisted chat history |
| `ClearCurrentChat()` | Clear in-memory session messages |
| `Status` | Current `AiState` (Idle / Listening / Thinking / Responding) |
| `Acknowledgement` | Get/set the response delivery mode |
| `StatusChanged` / `AiResponded` | Events for state changes and completed responses |

### Acknowledgement Modes

| Mode | Behavior |
|------|----------|
| `None` | No audio feedback or text-to-speech |
| `AudioBlip` | Short sound effects at state transitions |
| `LessWordy` | Text-to-speech with a "be concise" system prompt |
| `Full` | Text-to-speech with full unmodified responses |

### Architecture

```
┌─────────────────────────────────────────────────┐
│              IAiConversationService              │
│   (orchestrates chat, speech, sounds, history)   │
├──────────────┬──────────────┬────────────────────┤
│ IChatClientProvider │ IMessageStore │ ChatLookupAITool │
│ (default: DI)       │ (persistence) │ (optional AITool)│
│        IChatClient  │  ISpeechToText / ITextToSpeech   │
│        (M.E.AI)     │  (Shiny.Speech) · IAudioPlayer   │
│                     │  (Shiny.Audio)                   │
└─────────────────────────────────────────────────┘
```

## Dependencies

| Package | Purpose |
|---------|---------|
| [Microsoft.Extensions.AI](https://www.nuget.org/packages/Microsoft.Extensions.AI) | `IChatClient` abstraction |
| Shiny.Speech | Speech-to-text, text-to-speech, and audio playback (this repo) |

## Samples

- [MAUI sample](samples/MauiSample) — chat, settings, wake word, and animated aura visualization
- [Blazor sample](samples/BlazorSample) — the same features as Razor components (deployed to https://shinyorg.github.io/speech/)

## License

MIT
