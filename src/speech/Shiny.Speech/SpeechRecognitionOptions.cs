using System.Globalization;
using Shiny.Audio;

namespace Shiny.Speech;

public record SpeechRecognitionOptions
{
    public CultureInfo? Culture { get; init; }
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>
    /// Prefer recognition that runs locally, with no network round trip and no session length cap.
    /// Best-effort on every platform: iOS sets <c>RequiresOnDeviceRecognition</c> when the locale
    /// supports it, and Android uses the on-device recognizer when one is installed, falling back
    /// to the system recognizer with the offline hint set. A device without local recognition
    /// silently stays on the network path rather than failing.
    /// </summary>
    public bool PreferOnDevice { get; init; }
    public string[]? Keywords { get; init; }

    /// <summary>
    /// Microphone voice-processing effects (echo cancellation, noise suppression, automatic
    /// gain control) applied to the audio the recognizer listens to. Enable
    /// <see cref="AudioProcessingOptions.EchoCancellation"/> to stop text-to-speech output from
    /// bleeding into the mic during barge-in, or pass <see cref="AudioProcessingOptions.Analysis"/>
    /// to keep the signal unaltered and off a narrowband Bluetooth route.
    /// <para>
    /// Honored wherever the capture belongs to Shiny: every cloud provider (which records through
    /// <see cref="IAudioSource"/>) and the native iOS / Mac Catalyst / macOS recognizer, which owns
    /// its own <c>AVAudioEngine</c>. Leaving it <c>null</c> keeps the default those paths have
    /// always used — the full <see cref="AudioProcessingOptions.VoiceChat"/> chain — rather than
    /// raw capture.
    /// </para>
    /// <para>
    /// <b>Ignored on Android</b>, where recognition runs in the platform's own out-of-process
    /// <c>SpeechRecognizer</c> service: it opens the microphone itself and exposes no
    /// voice-processing controls, so there is no capture session to apply this to. Setting it there
    /// logs a warning. Use a cloud provider if the recognition path needs these effects on Android.
    /// </para>
    /// </summary>
    public AudioProcessingOptions? AudioProcessing { get; init; }
}
