using System.Globalization;
using Shiny.Audio;

namespace Shiny.Speech;

public record SpeechRecognitionOptions
{
    public CultureInfo? Culture { get; init; }
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public bool PreferOnDevice { get; init; }
    public string[]? Keywords { get; init; }

    /// <summary>
    /// Microphone voice-processing effects (echo cancellation, noise suppression, automatic
    /// gain control) applied when a provider captures audio via <see cref="IAudioSource"/>
    /// (e.g. the cloud providers). Enable <see cref="AudioProcessingOptions.EchoCancellation"/>
    /// to stop text-to-speech output from bleeding into the mic during barge-in. <c>null</c>
    /// captures raw audio. Native on-device recognizers manage their own microphone and are
    /// unaffected by this setting.
    /// </summary>
    public AudioProcessingOptions? AudioProcessing { get; init; }
}
