namespace Shiny.Audio;

/// <summary>
/// Volume adjustment with a soft limiter, so boosting a quiet mic does not turn peaks into crackle.
/// </summary>
public sealed class GainEffect : AudioEffect
{
    SmoothedParam gain = new(1f);

    /// <summary>Linear gain. 1.0 is unity, 2.0 is +6 dB, 0.5 is −6 dB. Clamped to 0–32.</summary>
    public float Gain
    {
        get => this.gain.Target;
        set => this.gain.Target = Math.Clamp(value, 0f, 32f);
    }

    /// <summary>The same control expressed in decibels.</summary>
    public float GainDb
    {
        get => 20f * MathF.Log10(MathF.Max(this.Gain, 1e-6f));
        set => this.Gain = Dsp.DbToLinear(value);
    }

    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        this.gain.BeginBlock(samples.Length, sampleRate);

        for (var i = 0; i < samples.Length; i++)
            samples[i] = Clip(Dsp.SoftClip(samples[i] * this.gain.Next()));
    }

    public override void Reset() => this.gain.Snap();
}
