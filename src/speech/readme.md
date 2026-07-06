# Shiny.Speech

Cross-platform speech services for .NET MAUI and Blazor WebAssembly — speech-to-text, text-to-speech, audio capture, and audio playback with pluggable cloud providers.

## Libraries

| Package | Description | Targets |
|---------|-------------|---------|
| **Shiny.Speech** | Core interfaces + native platform implementations (STT, TTS, audio capture, audio playback) | net10.0-ios, net10.0-android, net10.0-windows, net10.0 (Browser/WASM) |
| **Shiny.Speech.Cloud** | Cloud provider abstractions + `CloudSpeechToText` / `CloudTextToSpeech` implementations | net10.0 |
| **Shiny.Speech.Azure** | Azure AI Speech provider (STT + TTS) | net10.0 |
| **Shiny.Speech.ElevenLabs** | ElevenLabs provider (STT + TTS) | net10.0 |
| **Shiny.Speech.Typecast** | Typecast provider (TTS only) | net10.0 |

## Getting Started

### Native Platform Speech

Use the built-in OS speech engines — no cloud account needed. Works on MAUI (iOS, Android, Windows) and Blazor WebAssembly (via Web Speech API).

```csharp
builder.Services.AddSpeechServices();
// Registers: ISpeechToTextService, ITextToSpeechService, IAudioSource, IAudioPlayer
// On Browser/WASM: auto-detected via OperatingSystem.IsBrowser()
```

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
// Set a voice via TypecastConfig.DefaultVoiceId or TextToSpeechOptions.Voice;
// GetVoicesAsync() lists the ids available to your account.
```

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

No manifest changes needed — the browser prompts the user for microphone access automatically. Include the JS interop module in your `index.html`:
```html
<script src="shiny-speech.js"></script>
```

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
