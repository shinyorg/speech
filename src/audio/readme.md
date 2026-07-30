# Shiny.Audio

Cross-platform audio capture and playback for .NET MAUI, iOS, Mac Catalyst, macOS, Android, Windows,
Browser (WebAssembly), and Linux (via the companion `Shiny.Audio.Linux` package).

`Shiny.Audio` provides the low-level audio primitives that back [Shiny.Speech](https://shinylib.net/speech),
but is usable on its own:

- **`IAudioSource`** — microphone capture that streams raw PCM audio (16kHz, 16-bit, mono) via a
  thread-safe `PipeStream`, with runtime permission handling (`RequestAccess` / `AccessState`) and a
  normalized `InputLevelChanged` VU signal on every platform.
- **`IAudioPlayer`** — stream playback (e.g. MP3) with optional normalized `AudioLevelChanged`
  metering for VU-style UI.
- **`AudioLevel`** — the shared dBFS mapping behind every meter (`FromRms` / `FromPcm16` /
  `FromSamples`), so input and output bars read on the same scale — including PCM you meter yourself.

## Getting Started

```csharp
using Shiny;

builder.Services.AddAudioServices(); // registers IAudioSource + IAudioPlayer
```

```csharp
using Shiny.Audio;

public class Recorder(IAudioSource audioSource)
{
    public async Task RecordAsync(CancellationToken ct)
    {
        if (await audioSource.RequestAccess() != AccessState.Available)
            return;

        var pcmStream = await audioSource.StartCaptureAsync(ct);
        // consume pcmStream ...
        await audioSource.StopCaptureAsync();
    }
}
```

| Platform | Audio Capture | Audio Playback |
| --- | --- | --- |
| iOS 15+ | AVAudioEngine | AVAudioPlayer |
| Android 26+ | AudioRecord | MediaPlayer |
| Windows 10 19041+ | AudioGraph | MediaPlayer |
| Browser (WASM) | Web Audio API (`getUserMedia` + `ScriptProcessorNode`) | HTML5 `Audio` |
| Linux | PulseAudio / PipeWire (`pa_simple`), ALSA fallback | PulseAudio / PipeWire (`pa_simple`), ALSA fallback |

## Linux

Linux ships as a separate **`Shiny.Audio.Linux`** package (it carries a managed MP3 decoder, since
Linux has no system decoder to call). Add it and register before anything else:

```csharp
builder.Services.AddLinuxAudio();   // no-op when not running on Linux
```

It supplies all four services — `IAudioSource`, `IAudioPlayer`, `IAudioDevices` and `IAudioMonitor` —
over PulseAudio/PipeWire, falling back to ALSA on headless systems and containers.

See https://shinylib.net/speech for full documentation.
