# Shiny.Speech

Cross-platform speech services for .NET MAUI and Blazor WebAssembly — speech-to-text, text-to-speech, audio capture, and audio playback with pluggable cloud providers.

## Libraries

| Package | Description | Targets |
|---------|-------------|---------|
| **Shiny.Speech** | Core interfaces + native platform implementations (STT, TTS, audio capture, audio playback) | net10.0-ios, net10.0-android, net10.0-windows, net10.0 (Browser/WASM) |
| **Shiny.Speech.Cloud** | Cloud provider abstractions + `CloudSpeechToText` / `CloudTextToSpeech` implementations | net10.0 |
| **Shiny.Speech.Azure** | Azure AI Speech provider (STT + TTS) | net10.0 |
| **Shiny.Speech.ElevenLabs** | ElevenLabs provider (TTS) | net10.0 |

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
builder.Services.AddAudioSource();
builder.Services.AddAudioPlayer();
builder.Services.AddAzureSpeech("your-subscription-key", "your-region");
```

### ElevenLabs TTS (Cloud)

```csharp
builder.Services.AddAudioPlayer();
builder.Services.AddElevenLabsTextToSpeech("your-api-key");
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

### Speech-to-Text

```csharp
public class MyService(ISpeechToTextService stt)
{
    public async Task ListenAsync(CancellationToken ct)
    {
        var access = await stt.RequestAccess();
        if (access != AccessState.Available)
            return;

        // Check if already listening
        if (stt.IsListening)
            return;

        // Simple: wait for silence
        var text = await stt.ListenUntilSilence(cancellationToken: ct);

        // Streaming: get partial results
        await foreach (var result in stt.ContinuousRecognize(cancellationToken: ct))
        {
            Console.WriteLine($"[{(result.IsFinal ? "FINAL" : "partial")}] {result.Text}");
            if (result.IsFinal)
                break;
        }
    }
}
```

### Wake Word Listening

```csharp
// "Hey Siri" style — listens continuously until wake phrase is detected,
// then captures everything spoken after it until silence
var command = await stt.ListenWithWakeWord("Hey Computer", cancellationToken: ct);
// User says: "Hey Computer, what's the weather" → returns "what's the weather"
// User says: "Hey Computer" [pause] "what's the weather" → returns "what's the weather"
```

### Keyword Listening

```csharp
// Listens until one of the specified keywords is detected
var answer = await stt.ListenForKeyword(["Yes", "No", "Maybe"], cancellationToken: ct);
// User says: "I think yes" → returns "Yes"
```

## Custom Cloud Provider

Implement `ISpeechToTextProvider` and/or `ITextToSpeechProvider` from `Shiny.Speech.Cloud`:

```csharp
public class MyCloudSttProvider : ISpeechToTextProvider
{
    public async IAsyncEnumerable<SpeechRecognitionResult> RecognizeAsync(
        Stream audioStream,
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Read PCM audio from audioStream (16kHz, 16-bit, mono)
        // Yield recognition results...
    }
}

// Register:
builder.Services.AddAudioSource();
builder.Services.AddCloudSpeechToText<MyCloudSttProvider>();
```

## Platform Requirements

| Platform | STT | TTS | Audio Capture | Audio Playback |
|----------|-----|-----|---------------|----------------|
| iOS 15+ | SFSpeechRecognizer | AVSpeechSynthesizer | AVAudioEngine | AVAudioPlayer |
| Android 26+ | SpeechRecognizer | Android TTS | AudioRecord | MediaPlayer |
| Windows 10 19041+ | Windows.Media.SpeechRecognition | Windows.Media.SpeechSynthesis | AudioGraph | MediaPlayer |
| Browser (WASM) | Web Speech API (`SpeechRecognition`) | Web Speech API (`SpeechSynthesis`) | Not supported | HTML5 `Audio` |

### Browser (Blazor WebAssembly)

No manifest changes needed — the browser prompts the user for microphone access automatically. Include the JS interop module in your `index.html`:
```html
<script src="shiny-speech.js"></script>
```

> **Note:** `IAudioSource` (raw PCM capture) is not supported in the browser. The Web Speech API handles audio internally. Audio playback (`IAudioPlayer`) accepts any browser-supported format via a base64 data URL.

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
```
