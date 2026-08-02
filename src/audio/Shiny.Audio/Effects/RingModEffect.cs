namespace Shiny.Audio;

/// <summary>
/// Ring modulator — multiplies the voice by a sine carrier. At low carrier frequencies this is the
/// classic metallic robot/Dalek voice.
/// </summary>
public sealed class RingModEffect : AudioEffect
{
    SmoothedParam frequency = new(50f);
    SmoothedParam mix = new(1f);
    float phase;

    /// <summary>
    /// Carrier frequency in Hz. 20–80 gives the robot growl, several hundred gives clangorous
    /// inharmonic tones. Clamped to 1–4000. Default 50.
    /// </summary>
    public float Frequency
    {
        get => this.frequency.Target;
        set => this.frequency.Target = Math.Clamp(value, 1f, 4000f);
    }

    /// <summary>Wet/dry blend, 0–1. Default 1.</summary>
    public float Mix
    {
        get => this.mix.Target;
        set => this.mix.Target = Math.Clamp(value, 0f, 1f);
    }

    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        this.frequency.BeginBlock(samples.Length, sampleRate);
        this.mix.BeginBlock(samples.Length, sampleRate);

        var twoPi = 2f * MathF.PI;

        for (var i = 0; i < samples.Length; i++)
        {
            var carrier = MathF.Sin(this.phase);

            // Advance the phase, not an absolute time index, so changing Frequency mid-stream never
            // produces a discontinuity in the carrier.
            this.phase += twoPi * this.frequency.Next() / sampleRate;
            if (this.phase >= twoPi)
                this.phase -= twoPi;

            float dry = samples[i];
            samples[i] = Clip(dry + (dry * carrier - dry) * this.mix.Next());
        }
    }

    public override void Reset()
    {
        this.phase = 0f;
        this.frequency.Snap();
        this.mix.Snap();
    }
}
