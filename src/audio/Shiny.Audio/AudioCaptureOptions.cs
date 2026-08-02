namespace Shiny.Audio;

/// <summary>
/// Everything a capture session can be configured with: the platform's own voice processing, and a
/// chain of DSP effects applied to the PCM before it reaches you.
/// </summary>
public record AudioCaptureOptions
{
    /// <summary>
    /// Platform voice-processing effects (echo cancellation, noise suppression, automatic gain
    /// control) and route preferences. <c>null</c> captures raw input.
    /// </summary>
    public AudioProcessingOptions? Processing { get; init; }

    /// <summary>
    /// Effects applied to every captured buffer, in order, before it is written to the returned
    /// stream and before the level reported by <see cref="IAudioSource.InputLevelChanged"/> — so a
    /// meter shows what you will actually hear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keep the chain reference: toggling an effect or moving a parameter takes effect on the next
    /// buffer, with no need to restart capture. See <see cref="AudioEffectChain"/>.
    /// </para>
    /// <para>
    /// <b>Do not use this on audio bound for speech recognition.</b> Pitch, reverb and the rest
    /// destroy recognition and wake-word accuracy — for the same reason
    /// <see cref="AudioProcessingOptions.Analysis"/> exists, only more so.
    /// </para>
    /// </remarks>
    public AudioEffectChain? Effects { get; init; }

    /// <summary>Capture raw input with no processing and no effects.</summary>
    public static AudioCaptureOptions None => new();
}
