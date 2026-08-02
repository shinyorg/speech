namespace Shiny.Audio;

/// <summary>
/// Base class for <see cref="IAudioEffect"/> implementations. Handles the parts every effect would
/// otherwise repeat: atomic <see cref="Enabled"/> toggling and a click-free crossfade in and out of
/// bypass.
/// </summary>
/// <remarks>
/// Derived types implement <see cref="ProcessCore"/> and may assume it is only called when the
/// effect is audible. Hard-switching an effect mid-stream leaves a step discontinuity in the
/// waveform, which is heard as a pop; the crossfade here removes it, so a user can flip a switch
/// while recording without damaging the take.
/// </remarks>
public abstract class AudioEffect : IAudioEffect
{
    /// <summary>Time taken to fade in or out of bypass.</summary>
    const float BypassFadeMs = 12f;

    int enabled;
    float bypass;            // audio thread only: 0 = fully dry, 1 = fully wet
    short[] dryBuffer = [];

    protected AudioEffect(bool enabled = true)
    {
        this.enabled = enabled ? 1 : 0;
        this.bypass = enabled ? 1f : 0f;
    }

    /// <inheritdoc />
    public bool Enabled
    {
        get => Volatile.Read(ref this.enabled) != 0;
        set => Volatile.Write(ref this.enabled, value ? 1 : 0);
    }

    /// <inheritdoc />
    public void Process(Span<short> samples, int sampleRate)
    {
        if (samples.IsEmpty)
            return;

        var target = this.Enabled ? 1f : 0f;

        if (this.bypass == target)
        {
            if (target == 0f)
                return;                                 // settled in bypass — skip the work entirely

            this.ProcessCore(samples, sampleRate);      // settled in circuit — no blending needed
            return;
        }

        // Mid-fade: keep the dry signal so the two can be blended sample by sample.
        if (this.dryBuffer.Length < samples.Length)
            this.dryBuffer = new short[samples.Length];

        var dry = this.dryBuffer.AsSpan(0, samples.Length);
        samples.CopyTo(dry);
        this.ProcessCore(samples, sampleRate);

        var step = 1f / Math.Max(1, (int)(sampleRate * BypassFadeMs / 1000f));
        if (target == 0f)
            step = -step;

        var mix = this.bypass;
        for (var i = 0; i < samples.Length; i++)
        {
            mix = Math.Clamp(mix + step, 0f, 1f);
            float wet = samples[i];
            float d = dry[i];
            samples[i] = Clip(d + (wet - d) * mix);
        }
        this.bypass = mix;

        // Fully faded out: drop internal state so switching back on starts clean instead of
        // replaying a stale delay line or filter tail from minutes ago.
        if (this.bypass == 0f)
            this.Reset();
    }

    /// <summary>
    /// Transform the buffer in place. Only called while the effect is audible; the length must not
    /// change.
    /// </summary>
    protected abstract void ProcessCore(Span<short> samples, int sampleRate);

    /// <inheritdoc />
    public virtual void Reset()
    {
    }

    /// <summary>Clamp a floating-point sample into the PCM16 range.</summary>
    protected static short Clip(float sample)
        => (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
}
