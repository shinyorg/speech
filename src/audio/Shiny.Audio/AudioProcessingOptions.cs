namespace Shiny.Audio;

/// <summary>
/// Requests platform voice-processing effects on a microphone capture session.
/// Every effect is best-effort: a platform (or device driver) that cannot honor a
/// flag ignores it rather than failing. Feature availability is device-dependent on
/// Android and Windows.
/// </summary>
public record AudioProcessingOptions
{
    /// <summary>
    /// Acoustic echo cancellation — subtracts the device's own speaker output (e.g. a
    /// text-to-speech voice playing while the mic is open) from the captured signal.
    /// This is the effect that prevents the mic from re-capturing your TTS output.
    /// </summary>
    public bool EchoCancellation { get; init; }

    /// <summary>
    /// Noise suppression — attenuates steady background noise (fans, traffic, hum).
    /// </summary>
    public bool NoiseSuppression { get; init; }

    /// <summary>
    /// Automatic gain control — normalizes the captured level across quiet and loud speakers.
    /// </summary>
    public bool AutomaticGainControl { get; init; }

    /// <summary>
    /// True when at least one effect is requested.
    /// </summary>
    public bool AnyEnabled => this.EchoCancellation || this.NoiseSuppression || this.AutomaticGainControl;

    /// <summary>All effects enabled — the typical configuration for hands-free / barge-in voice UX.</summary>
    public static AudioProcessingOptions VoiceChat => new()
    {
        EchoCancellation = true,
        NoiseSuppression = true,
        AutomaticGainControl = true
    };

    /// <summary>No processing — capture raw microphone input.</summary>
    public static AudioProcessingOptions None => new();
}
