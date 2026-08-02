namespace Shiny.Audio;

/// <summary>
/// A real-time DSP effect applied to captured microphone audio.
/// </summary>
/// <remarks>
/// <para>
/// Effects are <b>caller-owned mutable objects</b>: you construct one, hand it to a capture or
/// recording session inside an <see cref="AudioEffectChain"/>, and keep the reference. Toggling
/// <see cref="Enabled"/> or assigning a parameter takes effect on the next audio buffer — there is
/// no "apply" step and no need to restart the session.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Process"/> runs on the platform's capture thread. Every parameter
/// setter is safe to call from any other thread (typically the UI thread) while processing is in
/// flight: parameters are single 32-bit fields, written atomically, and smoothed inside
/// <see cref="Process"/> so a moving slider does not produce clicks.
/// </para>
/// <para>
/// <b>Length is preserved.</b> <see cref="Process"/> works in place and never changes the sample
/// count — including <c>PitchShiftEffect</c>, which alters pitch while holding duration. Effects
/// that would change duration (time stretching) are deliberately not modelled, because on a live
/// stream they drift without bound.
/// </para>
/// </remarks>
public interface IAudioEffect
{
    /// <summary>
    /// Whether the effect contributes to the signal. Switching this crossfades over a few
    /// milliseconds rather than cutting, so toggling mid-capture is inaudible.
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>
    /// Process one buffer of mono PCM16 samples in place. Called on the capture thread.
    /// </summary>
    /// <param name="samples">The buffer to transform. Its length is not changed.</param>
    /// <param name="sampleRate">Sample rate of <paramref name="samples"/> — 16000 for capture.</param>
    void Process(Span<short> samples, int sampleRate);

    /// <summary>
    /// Drop any accumulated internal state (delay lines, filter memory, envelopes). Called when a
    /// session starts, and automatically when an effect finishes fading out.
    /// </summary>
    void Reset();
}
