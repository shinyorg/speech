namespace Shiny.Audio;

/// <summary>
/// Chorus/flanger — mixes the voice with a copy whose delay is swept by a slow LFO, giving a
/// doubled, shimmering "more than one person" quality. Short delays and higher feedback move it
/// from chorus toward flanging.
/// </summary>
public sealed class ChorusEffect : AudioEffect
{
    const float BaseDelayMs = 18f;
    const float MaxDelayMs = BaseDelayMs + 30f;

    SmoothedParam rateHz = new(0.8f);
    SmoothedParam depthMs = new(6f);
    SmoothedParam mix = new(0.5f);
    SmoothedParam feedback = new(0f);

    DelayLine? line;
    float phase;

    /// <summary>LFO speed in Hz. Clamped to 0.05–10. Default 0.8.</summary>
    public float RateHz
    {
        get => this.rateHz.Target;
        set => this.rateHz.Target = Math.Clamp(value, 0.05f, 10f);
    }

    /// <summary>How far the delay is swept, in milliseconds. Clamped to 0.5–25. Default 6.</summary>
    public float DepthMs
    {
        get => this.depthMs.Target;
        set => this.depthMs.Target = Math.Clamp(value, 0.5f, 25f);
    }

    /// <summary>Wet/dry blend, 0–1. Default 0.5 — chorus needs the dry signal to beat against.</summary>
    public float Mix
    {
        get => this.mix.Target;
        set => this.mix.Target = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// Recirculation, 0–0.9. Zero is chorus; raising it produces the sharp resonant sweep of a
    /// flanger. Default 0.
    /// </summary>
    public float Feedback
    {
        get => this.feedback.Target;
        set => this.feedback.Target = Math.Clamp(value, 0f, 0.9f);
    }

    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        var capacity = (int)(MaxDelayMs * 0.001f * sampleRate) + 4;
        if (this.line == null)
            this.line = new DelayLine(capacity);
        else
            this.line.EnsureCapacity(capacity);

        this.rateHz.BeginBlock(samples.Length, sampleRate);
        this.depthMs.BeginBlock(samples.Length, sampleRate);
        this.mix.BeginBlock(samples.Length, sampleRate);
        this.feedback.BeginBlock(samples.Length, sampleRate);

        var twoPi = 2f * MathF.PI;

        for (var i = 0; i < samples.Length; i++)
        {
            // Sine LFO around a fixed base delay; the phase accumulator keeps the sweep continuous
            // when the rate changes.
            var sweep = (MathF.Sin(this.phase) * 0.5f + 0.5f) * this.depthMs.Next();
            this.phase += twoPi * this.rateHz.Next() / sampleRate;
            if (this.phase >= twoPi)
                this.phase -= twoPi;

            var delaySamples = (BaseDelayMs + sweep) * 0.001f * sampleRate;
            var delayed = this.line.Read(delaySamples);

            float dry = samples[i];
            this.line.Write(dry + delayed * this.feedback.Next());

            samples[i] = Clip(dry + (delayed - dry) * this.mix.Next());
        }
    }

    public override void Reset()
    {
        this.line?.Clear();
        this.phase = 0f;
        this.rateHz.Snap();
        this.depthMs.Snap();
        this.mix.Snap();
        this.feedback.Snap();
    }
}
