# Shiny.Audio

Cross-platform audio capture and playback for .NET MAUI, iOS, Mac Catalyst, macOS, Android, Windows,
and Browser (WebAssembly).

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

See https://shinylib.net/speech for full documentation.
