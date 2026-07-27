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
    /// Allow capture to route to a paired Bluetooth headset. Default <c>true</c>.
    /// <para>
    /// Set this <c>false</c> when the captured audio is <i>analysed</i> rather than listened to —
    /// speaker recognition/verification, keyword spotting, anything feeding an embedding model. A
    /// Bluetooth mic runs over HFP, which caps capture at 8 kHz narrowband, so the bandwidth you
    /// record silently depends on what happens to be paired.
    /// </para>
    /// </summary>
    /// <remarks>
    /// iOS/Mac Catalyst only — it drops the Bluetooth options from the audio session category.
    /// Android's raw mic source and Windows capture never route to a Bluetooth mic implicitly.
    /// </remarks>
    public bool AllowBluetooth { get; init; } = true;

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

    /// <summary>
    /// No processing and no Bluetooth route — capture the built-in mic as unaltered as the platform
    /// allows. Use this when the signal feeds a model (speaker embeddings, wake words): the effects in
    /// <see cref="VoiceChat"/> are adaptive and non-linear, so they change the very characteristics
    /// such models measure, and two recordings of one person come out different.
    /// </summary>
    public static AudioProcessingOptions Analysis => new() { AllowBluetooth = false };
}
