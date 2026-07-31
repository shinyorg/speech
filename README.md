# Shiny.Speech &amp; Shiny.AiConversation

This repository is the home for two complementary library families:

- **Shiny.Speech** — Cross-platform speech services for .NET MAUI and Blazor WebAssembly: speech-to-text and text-to-speech with pluggable cloud providers. Audio capture and playback are provided by the standalone **Shiny.Audio** package (referenced automatically).
- **Shiny.Audio** — Cross-platform microphone capture (`IAudioSource`) and stream playback (`IAudioPlayer`) with VU-level metering, plus a live mic-to-output **monitor** (`IAudioMonitor`) and audio **route enumeration/selection** (`IAudioDevices`), all discoverable through one `IAudio` facade. Usable on its own; also the audio backbone for Shiny.Speech.
- **Shiny.AiConversation** — A centralized AI service that orchestrates chat, speech recognition, wake word detection, text-to-speech, and persistent message history into a single `IAiConversationService`. AiConversation drives much of the real-world feature set (and bug surface) of the speech stack, which is why both live and ship from here together.

All packages share a single version, defined by `version.json` at the repo root (Nerdbank.GitVersioning).

## Libraries

| Package | Description | Targets |
|---------|-------------|---------|
| **Shiny.Audio** | Standalone audio capture (`IAudioSource`) + playback (`IAudioPlayer`) + live mic monitor (`IAudioMonitor`) + route enumeration (`IAudioDevices`) behind the `IAudio` facade, with native platform implementations | net10.0-ios, net10.0-android, net10.0-windows, net10.0 (Browser/WASM) |
| **Shiny.Audio.Linux** | Linux backend for all four audio services over PulseAudio/PipeWire with an ALSA fallback. Ships separately because it carries a managed MP3 decoder (`NLayer`) — Linux has no system decoder to call | net10.0 |
| **Shiny.Speech** | Core STT/TTS interfaces + native platform implementations (references Shiny.Audio for capture/playback) | net10.0-ios, net10.0-android, net10.0-windows, net10.0 (Browser/WASM) |
| **Shiny.Speech.Cloud** | Cloud provider abstractions + `CloudSpeechToText` / `CloudTextToSpeech` implementations | net10.0 |
| **Shiny.Speech.Azure** | Azure AI Speech provider (STT + TTS) | net10.0 |
| **Shiny.Speech.ElevenLabs** | ElevenLabs provider (STT + TTS) | net10.0 |
| **Shiny.Speech.Typecast** | Typecast provider (TTS only, via the `typecast-csharp` SDK) | net10.0 |
| **Shiny.Speech.Linux.Whisper** | On-device, offline STT for Linux (incl. Raspberry Pi) running Whisper locally through whisper.cpp — no cloud account, no network | net10.0 |
| **Shiny.AiConversation** | Central `IAiConversationService` orchestrating chat + the full voice loop | net10.0 (+ MAUI platforms) |
| **Shiny.AiConversation.Maui** | `AiChatView` — a .NET MAUI chat control (built on Shiny.Maui.Controls `ChatView`) already wired to `IAiConversationService`, including message-store history | net10.0 (MAUI) |
| **Shiny.AiConversation.OpenAi** | Ready-made static OpenAI-compatible chat client provider | net10.0 |
| **Shiny.AiConversation.Maui.GithubCopilot** | MAUI GitHub Copilot provider (device-code OAuth, SecureStorage) | net10.0 (MAUI) |
| **Shiny.AiConversation.MessageStores.SqliteDocDb** | SQLite/DocumentDb-backed `IMessageStore` for persistent chat history | net10.0 |

## Getting Started

### Native Platform Speech

Use the built-in OS speech engines — no cloud account needed. Works on MAUI (iOS, Android, Windows) and Blazor WebAssembly (via Web Speech API).

```csharp
builder
    .UseMauiApp<App>()
    .UseShiny(); // required — see note below

builder.Services.AddSpeechServices();
// Registers: ISpeechToTextService, ITextToSpeechService, IAudioSource, IAudioPlayer
// On Browser/WASM: auto-detected via OperatingSystem.IsBrowser()
```

> **`UseShiny()` is now required for native speech/audio.** Runtime permission handling (Android
> `RECORD_AUDIO`) and activity tracking are delegated to [Shiny.Core](https://shinylib.net)'s
> `AndroidPlatform` instead of a hand-rolled fragment. Add the `Shiny.Hosting.Maui` package and call
> `.UseShiny()` on the `MauiAppBuilder` so `AndroidPlatform` is registered and receives permission
> callbacks. This replaces the old self-contained `ActivityProvider`/`PermissionRequestFragment`.

> **`AccessState` now comes from `Shiny.Core`.** It lives in the `Shiny` namespace (it previously lived
> in `Shiny.Audio`). Because `Shiny` is a parent of `Shiny.Audio`/`Shiny.Speech`, most code needs no
> change; add `using Shiny;` only where you reference it outside those namespaces. `IAudioSource`,
> `IAudioPlayer`, and `PipeStream` remain in `Shiny.Audio` — add `using Shiny.Audio;` where you consume
> them. `AddSpeechServices()` still wires up capture + playback; to register audio on its own — including
> the live monitor (`IAudioMonitor`), route enumeration (`IAudioDevices`), and the `IAudio` facade — call
> `builder.Services.AddAudioServices();`.

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

### VU Meters (Audio Levels)

Both directions are metered, on the same normalized `0.0`–`1.0` scale (dBFS mapped from a -50 dB
noise floor, so voice actually moves the bar).

**Outgoing — playback / TTS.** `ITextToSpeechService` and `IAudioPlayer` raise `AudioLevelChanged`
while audio is playing. Gate UI on `IsPlayerAnalysisSupported`.

```csharp
if (tts.IsPlayerAnalysisSupported)
    tts.AudioLevelChanged += (s, level) =>
        MainThread.BeginInvokeOnMainThread(() => SpeakingBar.Progress = level);
```

**Incoming — microphone.** `ISpeechToTextService` raises `InputLevelChanged` while listening — gate
UI on `IsInputAnalysisSupported`. The lower-level `IAudioSource.InputLevelChanged` (raw capture) and
`IAudioMonitor.InputLevelChanged` (live mic-to-output) emit the same signal.

```csharp
if (stt.IsInputAnalysisSupported)
    stt.InputLevelChanged += (s, level) =>
        MainThread.BeginInvokeOnMainThread(() => ListeningBar.Progress = level);
```

| Surface | iOS / macOS | Android | Windows | Browser | Linux |
|---|---|---|---|---|---|
| Native `ITextToSpeechService` | ✅ | ✅ | ❌ | ❌ | n/a |
| Cloud `ITextToSpeechService` (Azure / OpenAI / ElevenLabs / custom) | ✅ | ✅ | ❌ | ❌ | ✅ |
| `IAudioPlayer` (generic playback) | ✅ | ✅ | ❌ | ❌ | ✅ |
| Cloud `ISpeechToTextService` (Azure / OpenAI / ElevenLabs / custom) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Native `ISpeechToTextService` | ✅ | ✅ | ❌ | ❌ | n/a |
| On-device `ISpeechToTextService` (Whisper) | ❌ | ❌ | ❌ | ❌ | ✅ |
| `IAudioSource` (raw capture) | ✅ | ✅ | ✅ | ✅ | ✅ |
| `IAudioMonitor` (live monitor) | ✅ | ✅ | n/a | n/a | ✅ |

Cloud recognition meters the `IAudioSource` feeding the provider, so it works everywhere. Native
recognition depends on the platform: Apple taps the recognizer's own input node, Android reports the
`SpeechRecognizer` RMS callback, while Windows' and the browser's recognizers own the mic and expose
no level at all. Linux has no OS speech engine to wrap, so there is no native STT/TTS there at all —
but every cloud provider works, `Shiny.Speech.Linux.Whisper` covers offline recognition, and playback
metering is supported because the Linux player decodes to PCM in managed code and sees every sample.

On Apple platforms, native TTS routes `AVSpeechSynthesizer` through `AVAudioEngine` +
`AVAudioPlayerNode` so audio levels can be tapped. The engine is created lazily on first speak and
kept warm across utterances — only the first utterance pays ~50–150 ms additional startup.

Levels are raised off the UI thread and throttled to ~20/sec on the capture side; marshal before
binding. Reset your bound value to `0` when playback/listening ends so the meter drains.

### Volume

`IAudioPlayer` exposes `Volume` (the device media volume, normalized `0.0`–`1.0`), a
`VolumeChanged` event, and `IsVolumeControlSupported`. **Reading works on every platform; setting is
platform-limited** — always guard a set with `IsVolumeControlSupported`.

```csharp
// Read anywhere
var level = player.Volume;

// Set only where supported (throws NotSupportedException on iOS / Mac Catalyst)
if (player.IsVolumeControlSupported)
    player.Volume = 0.5f;

// Observe changes: hardware buttons, the OS volume UI, or a successful set
player.VolumeChanged += (_, v) =>
    MainThread.BeginInvokeOnMainThread(() => MyVolumeSlider.Value = v);
```

| Platform | Read | Set | `VolumeChanged` source | Backing API |
|---|---|---|---|---|
| Android | ✅ | ✅ | System settings observer | `AudioManager` `STREAM_MUSIC` |
| Windows | ✅ | ✅ | Endpoint volume callback | WASAPI `IAudioEndpointVolume` (default render endpoint) |
| macOS | ✅ | ✅ * | CoreAudio property listener | HAL virtual main volume of the default output device |
| iOS / Mac Catalyst | ✅ | ❌ | KVO on `outputVolume` | `AVAudioSession.OutputVolume` (read-only) |
| Browser (WASM) | ✅ | ✅ | Echoed on set | `HTMLAudioElement.volume` (app-local, **not** the OS volume) |
| Linux (PulseAudio / PipeWire) | ✅ | ✅ | Server subscription | Default sink volume via `pa_context_set_sink_volume_by_name` |
| Linux (ALSA fallback) | ✅ † | ❌ | n/a | n/a — binding `snd_mixer` isn't worth it for the fallback path |

\* macOS is settable when the current default output device exposes a settable virtual main volume
(most built-in / USB devices do; some HDMI / aggregate devices don't) — this is reflected by
`IsVolumeControlSupported`.

† On the ALSA fallback `Volume` reads back the last value set in-process (defaulting to `1.0`) rather
than a real device level, and `IsVolumeControlSupported` is `false` so the setter throws. Check it
before assigning, as on iOS.

On device platforms `Volume` is the **system media volume** the hardware buttons control, independent
of any per-request TTS volume. On iOS / Mac Catalyst there is no supported OS API to change the system
volume, so the setter throws — let the user adjust it via the hardware buttons or an `MPVolumeView`.
Browsers sandbox the OS volume, so there `Volume` is the app's own media-element volume (settable, and
it persists across plays).

### Microphone Monitor & Routes (`IAudio`)

Inject the single `IAudio` facade to reach the whole audio surface — `Player`, `Source`, `Monitor`,
`Devices` — or keep injecting the focused interfaces directly. `IAudioMonitor` routes the mic straight
to the current output in near-real-time (a PA / "talk over a Bluetooth speaker"); `IAudioDevices`
reports which routes are active.

```csharp
using Shiny.Audio;
using Shiny; // AccessState

public class PaController(IAudio audio)
{
    public async Task Start()
    {
        if (await audio.Monitor.RequestAccess() != AccessState.Available)
            return;

        audio.Monitor.InputLevelChanged += (_, level) => { /* drive a VU bar */ };

        // No Processing → routes to a Bluetooth A2DP speaker (phone mic + BT output).
        await audio.Monitor.Start(new AudioMonitorOptions { Gain = 1.0 });
    }

    // Display where audio is flowing (e.g. "JBL Flip · BluetoothA2dp").
    public string Output => $"{audio.Devices.CurrentOutput?.Name} · {audio.Devices.CurrentOutput?.Type}";

    public Task Stop() => audio.Monitor.Stop();
}
```

`IAudioMonitor` and `IAudioDevices` are implemented on **iOS/Mac Catalyst and Android**; the facade
throws `PlatformNotSupportedException` if you touch them elsewhere.

#### Classifying the route

`AudioDevice.Type` normalizes the OS route into `AudioDeviceType` — including wired: `WiredHeadphones`
(output only) and `WiredHeadset` (output **plus** mic). Rather than matching every variant by hand, use
the classification helpers:

```csharp
var output = audio.Devices.CurrentOutput;

if (output?.IsWired() == true)      { /* 3.5mm jack, Lightning, or USB-C */ }
if (output?.IsBluetooth() == true)  { /* HFP/SCO or A2DP */ }
if (output?.IsBuiltIn() == true)    { /* phone speaker or earpiece — nothing attached */ }

// "Is audio private to the user?" — e.g. before speaking a TTS response out loud.
if (output?.IsHeadphones() == true)
    await audio.Player.Play(reply);

// Will capture come from the accessory, or fall back to the phone mic?
var accessoryMic = output?.HasMicrophone() == true;
```

Each helper also exists on `AudioDeviceType` directly. Subscribe to `IAudioDevices.Changed` to react to
plug/unplug — it fires on both platforms (iOS route-change notification, Android device callback).

- **`IsWired()` includes `Usb`.** On handsets with no 3.5mm jack the wired option *is* USB-C, and neither
  platform reports those as a `Wired*` type (Android says `UsbHeadset`/`UsbDevice`, iOS says
  `PortUsbAudio`). The trade-off: a USB audio interface or DAC also answers true. Use `IsHeadphones()`
  when you specifically mean something worn on the head, accepting that it misses USB-C earbuds.
- **Bluetooth can't be narrowed.** A paired A2DP route is equally a set of earbuds or a room speaker;
  no platform API distinguishes them, so `IsHeadphones()` counts all Bluetooth as private.

- **Bluetooth speaker vs. echo cancellation (iOS):** enabling `AudioProcessingOptions` forces Bluetooth
  onto the low-quality HFP call profile, so a Bluetooth *speaker* (A2DP) drops back to the phone. Leave
  processing **off** to reach a BT speaker; turn it on only for phone-speaker output where feedback is a
  problem. **AirPlay/HomePod is not supported for a live mic** — use Bluetooth for a wireless PA.
- **Device selection is Android-first:** Android enumerates/selects both input and output; iOS can
  select the input but treats **output as observe-only** (AirPlay/Bluetooth output routing is owned by
  the system picker). Use `CurrentInput`/`CurrentOutput` as a **display** property everywhere.

See the **Microphone** tab in the MAUI sample for a full page.

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

// capture for a *model* (speaker recognition, wake words) rather than a listener:
var raw = await audioSource.StartCaptureAsync(AudioProcessingOptions.Analysis);
```

`AudioProcessingOptions.Analysis` is `None` plus `AllowBluetooth = false`, and it matters whenever the
audio feeds an embedding model. The effects above are adaptive and exist to normalize away speaker and
channel characteristics — the very thing such a model measures — so two recordings of one person come
back different; and a Bluetooth mic runs over HFP at 8 kHz narrowband, which quietly makes the captured
bandwidth depend on what is paired. On iOS/Mac Catalyst, requesting no effects puts the session in
`Measurement` mode (minimum system input processing) rather than `VoiceChat`.

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
| Linux (`Shiny.Audio.Linux`) | Whisper on-device (`Shiny.Speech.Linux.Whisper`) or any cloud provider | ❌ native — use a cloud provider | PulseAudio / PipeWire (`pa_simple`), ALSA fallback | PulseAudio / PipeWire (`pa_simple`), ALSA fallback |

### Linux

Linux support lives in a separate **`Shiny.Audio.Linux`** package. It provides all four audio
services — capture, playback, device enumeration and live mic monitoring — over PulseAudio/PipeWire,
falling back to ALSA where no sound server is running (headless boxes, minimal containers, Raspberry
Pi images).

```csharp
using Shiny;

// Call this BEFORE AddSpeechServices() / AddCloudSpeechToText<T>() — those use TryAdd,
// so whatever is registered first wins. It is a no-op when not running on Linux, so it
// is safe to leave in shared startup code.
builder.Services.AddLinuxAudio();

builder.Services.AddCloudSpeechToText<AzureSpeechToTextProvider>();
builder.Services.AddCloudTextToSpeech<AzureTextToSpeechProvider>();
```

**There is no native Linux STT/TTS** — Linux has no OS speech engine to wrap the way iOS, Android and
Windows do, so `AddSpeechToText()` / `AddTextToSpeech()` register nothing there. Use any of the cloud
providers (Azure, OpenAI, ElevenLabs, Typecast, Microsoft.Extensions.AI); they are pure HTTP over the
PCM stream and work unchanged once `AddLinuxAudio()` has supplied an `IAudioSource`. For recognition
without a cloud account at all, see **Whisper on-device** below.

Notes:

- **No resampler ships in this library.** Streams are opened at exactly the format required (16 kHz
  mono for capture, a decoded clip's native rate for playback); PulseAudio converts server-side and
  ALSA's `default` device converts through its `plug` plugin.
- **Playback decodes in managed code.** Linux has no system media decoder to hand a stream to, so
  MP3 (what Azure, OpenAI and ElevenLabs return by default) is decoded with `NLayer`, and WAV/PCM
  directly. Other containers throw `NotSupportedException` naming the format.
- **Voice processing is best-effort.** PulseAudio exposes echo cancellation as a virtual source from
  `module-echo-cancel`; when it is loaded, `AudioProcessingOptions` with `EchoCancellation` selects
  it, otherwise capture is raw.
- **Runtime dependencies** are the distro's own: `libpulse-simple.so.0` (usually `libpulse0`) or
  `libasound.so.2` (usually `libasound2`). Check `LinuxAudioServiceCollectionExtensions.IsLinuxAudioAvailable`
  at startup to fail fast where neither is present.
- `IAudioDevices.ShowOutputPicker()` is a no-op — Linux has no system route picker equivalent to
  iOS's `AVRoutePickerView`; routing lives in the desktop environment's sound settings. `Changed`
  fires on PulseAudio/PipeWire only (ALSA has no change notification).

### Whisper on-device (Linux)

**`Shiny.Speech.Linux.Whisper`** runs OpenAI's Whisper locally through
[whisper.cpp](https://github.com/ggerganov/whisper.cpp) (via
[Whisper.net](https://github.com/sandrohanea/whisper.net)) — the closest thing Linux has to the
native recognizers iOS, Android and Windows ship with. No cloud account, no API key, no network at
runtime, no per-minute billing.

```csharp
using Shiny;
using Shiny.Speech.Linux;
using Whisper.net.Ggml;

builder.Services.AddLinuxAudio();               // must come first — supplies IAudioSource
builder.Services.AddLinuxWhisperSpeechToText(GgmlType.BaseEn, QuantizationType.Q5_1);

// or with full control:
builder.Services.AddLinuxWhisperSpeechToText(new WhisperConfig
{
    ModelType = GgmlType.BaseEn,
    Quantization = QuantizationType.Q5_1,
    InitialPrompt = "Shiny, MAUI, Blazor",      // bias the decoder toward your vocabulary
    SilenceRmsThreshold = 500                   // VAD sensitivity
});
```

It registers a normal `ISpeechToTextService`, so `ListenUntilSilence()`, `StatementAfterKeyword()`
and the rest of the extension methods work unchanged. Registration is a **no-op off Linux**, so it is
safe to leave in shared startup code.

The model is downloaded from Hugging Face on first use and cached in
`~/.local/share/shiny.speech/whisper`. That first call costs a download plus several seconds of model
load, so call `PrepareAsync()` at startup rather than making the user's first utterance pay for it:

```csharp
var provider = host.Services.GetRequiredService<ISpeechToTextProvider>();
await ((WhisperSpeechToTextProvider)provider).PrepareAsync();
```

Notes:

- **Speech-to-text only.** Whisper is a recognition model; there is no Whisper TTS. Pair it with a
  cloud TTS provider, or skip TTS entirely.
- **No partial results.** Whisper is a batch model with a 30-second window, not a streaming
  recognizer. The provider runs client-side voice activity detection over the mic stream and turns
  each speech→silence segment into one inference and one `IsFinal = true` result — the same shape as
  the ElevenLabs Scribe provider. Tune with `SilenceRmsThreshold`, `MinUtteranceDurationMs` and
  `MaxUtteranceDurationMs`.
- **Model sizing.** `Tiny`/`Base` are the realistic choices on a Raspberry Pi 4/5 — expect roughly
  realtime at `Base`, slower above `Small`. This suits push-to-talk and wake-word-then-command far
  better than continuous dictation. On x64 desktops/servers, `Small` and up are comfortable. The
  `*En` variants are meaningfully more accurate than the multilingual model of the same size when you
  only need English.
- **Platform support is all mainstream Linux, not just Pi** — `linux-x64`, `linux-arm64` and
  `linux-arm` natives all ship. Requirements are `libstdc++6` and glibc 2.31+ (Debian 11+ / Ubuntu
  20.04+ / Raspberry Pi OS Bullseye+). On **x86/x64 the CPU must support AVX, AVX2, FMA and F16C**;
  older CPUs need an added `Whisper.net.Runtime.NoAvx` package reference. ARM has no such
  requirement.
- **Trim the natives on publish.** The bundled `Whisper.net.Runtime` carries binaries for every
  platform it supports; publishing with an explicit RID (`dotnet publish -r linux-arm64`) copies only
  the ones you need.
- **GPU is opt-in.** Add `Whisper.net.Runtime.Cuda` (or `.Vulkan`) and set `UseGpu = true`; the CPU
  runtime bundled here ignores the flag.
- **Hallucination guards are on by default.** Whisper emits `[BLANK_AUDIO]` and `(wind blowing)`-style
  annotations for silence and noise; `FilterNonSpeechAnnotations` strips them and drops results that
  filter down to nothing. `CarryContextBetweenUtterances` is off so a bad transcription can't poison
  everything after it.

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

## MAUI Chat UI (`Shiny.AiConversation.Maui`)

`AiChatView` is a drop-in chat screen for the conversation service. It derives from the
[Shiny.Maui.Controls](https://shinylib.net/client/controls/) `ChatView`, so every style, template and
behavior property of the base control still applies — but the provider, session, history paging and
live AI events are already wired up. There is nothing to bind.

```bash
dotnet add package Shiny.AiConversation.Maui
```

```xml
<ContentPage xmlns:ai="http://shiny.net/maui/aiconversation">
    <ai:AiChatView BotName="Aura"
                   BotAvatar="bot.png"
                   GreetingMessage="Hi! What can I help you with?"
                   ShowMicrophoneAction="True"
                   ShowTokenUsage="True"

                   MyBubbleColor="{StaticResource Primary}"
                   MyTextColor="White"
                   OtherBubbleColor="#F0EEFF"
                   BubbleCornerRadius="16"
                   PlaceholderText="Ask me something..." />
</ContentPage>
```

What it wires for you:

- **Sending** — typed messages go to `IAiConversationService.TalkTo`; replies arrive on `AiResponded` and render as bubbles
- **Voice** — utterances heard by speech-to-text (wake word *or* push-to-talk) render as user bubbles via `SpeechOccurred`; `ShowMicrophoneAction` adds an input-bar action that calls `ListenAndTalk`
- **History** — the chat is backfilled from the registered `IMessageStore` and pages further back on scroll-to-top. No message store registered? The chat simply starts empty and stays live-only
- **Typing indicator** — driven by `AiState` (`Thinking` / `Responding`), with a heartbeat so long turns keep the bubble alive
- **Errors** — failures from `TalkTo` and the service's `ErrorOccurred` event render as AI bubbles (`Identifier = "error"`)

### AiChatView properties

| Property | Default | Description |
|----------|---------|-------------|
| `AiService` | resolved from DI | The `IAiConversationService` to drive. Leave unset to resolve from the app's service provider |
| `BotName` | `Assistant` | Display name of the AI (also the chat session name) |
| `BotAvatar` | `null` | `ImageSource` for the AI |
| `BotBubbleColor` | `null` | Per-user bubble color for the AI; falls back to `OtherBubbleColor` |
| `UserName` / `UserAvatar` / `UserBubbleColor` | `Me` / `null` / `null` | The device user's identity; bubble color falls back to `MyBubbleColor` |
| `LoadHistory` | `true` | Backfill + page history from the message store |
| `GreetingMessage` | `null` | AI message shown when there's no history |
| `ShowTokenUsage` | `false` | Appends a token usage footer to AI messages when the provider reports usage |
| `ShowMicrophoneAction` | `false` | Adds a push-to-talk action to the input bar (invoke again to cancel) |
| `MicrophoneActionText` | `🎤 Voice Input` | Label of that action |
| `Refresh()` | — | Method — reloads the conversation (use after `ClearChatHistory`) |

Everything else — `MyBubbleColor`, `OtherTextColor`, `ChatBackgroundColor`, `BubbleFontSize`,
`BubbleFontFamily`, `BubbleCornerRadius`, `TimestampFontSize`, `PlaceholderText`, `SendButtonText`,
`SendButton*`/`InputBar*` colors, `IsInputBarVisible`, `ShowTypingIndicator`, `MessageTemplate`,
`MessageTemplateSelector`, `InputActions`, `CustomBubbleActions`, `PageSize`, `UseFeedback`,
`AdjustForKeyboard` — is inherited from `ChatView` and documented with it.

> Hosting the chat inside a `FloatingPanel`? Set `AdjustForKeyboard="False"`, same as the base control.

To drive a plain `ChatView` yourself instead, register the provider and bind it:

```csharp
builder.Services.AddShinyAiConversation(opts =>
{
    opts.AddGithubCopilotChatClient();
    opts.AddChatSessionProvider(cfg => cfg.BotName = "Aura"); // registers IChatSessionProvider
});
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
