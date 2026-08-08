# Shiny.Audio

Cross-platform audio capture and playback for .NET MAUI, iOS, Mac Catalyst, macOS, Android, Windows,
Browser (WebAssembly), and Linux (via the companion `Shiny.Audio.Linux` package).

`Shiny.Audio` provides the low-level audio primitives that back [Shiny.Speech](https://shinylib.net/speech),
but is usable on its own:

- **`IAudioSource`** — microphone capture that streams raw PCM audio (16kHz, 16-bit, mono) via a
  thread-safe `PipeStream`, with runtime permission handling (`RequestAccess` / `AccessState`) and a
  normalized `InputLevelChanged` VU signal on every platform.
- **`IAudioPlayer`** — stream / file / URL playback (e.g. MP3) with optional normalized
  `AudioLevelChanged` metering for VU-style UI. Clips play **concurrently**: `StartAsync` returns an
  `IAudioPlayback` you can stop on its own, `PlayAsync` waits for one clip without interrupting the
  rest, and `StopAsync` stops the lot.
- **`IAudioRecorder`** — record the microphone straight to a WAV file, optionally through a live
  effect chain, capturing the processed take, the raw one, or both.
- **`AudioEffectChain`** — real-time DSP on capture (pitch shift, echo, reverb, filters, distortion,
  ring modulation, chorus, gain, noise gate) that you toggle and re-tune *while recording*.
- **`AudioLevel`** — the shared dBFS mapping behind every meter (`FromRms` / `FromPcm16` /
  `FromSamples`), so input and output bars read on the same scale — including PCM you meter yourself.
- **`WavWriter` / `WavReader`** — streaming RIFF/WAVE PCM I/O, plus `AudioEffectProcessor` for
  applying an effect chain to a file that already exists.

## Getting Started

```csharp
using Shiny;

builder.Services.AddAudioServices(); // registers IAudioSource, IAudioPlayer, IAudioRecorder, ...
```

```csharp
using Shiny.Audio;

public class Recorder(IAudioSource audioSource)
{
    public async Task RecordAsync(CancellationToken ct)
    {
        if (await audioSource.RequestAccess() != AccessState.Available)
            return;

        var pcmStream = await audioSource.StartCaptureAsync(cancellationToken: ct);
        // consume pcmStream ...
        await audioSource.StopCaptureAsync();
    }
}
```

## Effects & recording

Effects are caller-owned objects. Build a chain, hand it to a capture or recording session, and keep
the reference — toggling an effect or moving a parameter applies on the next audio buffer, with no
restart and no clicks.

```csharp
using Shiny.Audio;

var chain = new AudioEffectChain();
var pitch = chain.Add(new PitchShiftEffect { Semitones = 0 });
var echo  = chain.Add(new EchoEffect { Enabled = false });

await recorder.StartAsync(new AudioRecordingOptions
{
    Mode = AudioRecordMode.Both,   // writes the processed and the raw take
    Effects = chain
});

pitch.Semitones = 5;      // heard immediately
echo.Enabled = true;
chain.Enabled = false;    // master bypass

var recording = await recorder.StopAsync();
```

Available effects: `GainEffect`, `NoiseGateEffect`, `BiquadFilterEffect`, `DistortionEffect`,
`RingModEffect`, `EchoEffect`, `ChorusEffect`, `ReverbEffect`, `PitchShiftEffect` — plus
`AudioEffectPresets.Create(...)` for ready-made combinations (Robot, Chipmunk, DeepVoice, Cathedral,
Telephone, Megaphone, Ensemble).

> **Do not put effects on audio bound for speech recognition** — they destroy recognition and
> wake-word accuracy.

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
