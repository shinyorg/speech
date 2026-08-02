namespace Shiny.Audio;

/// <summary>
/// Overdrive: pushes the signal into a <c>tanh</c> saturation curve for a gritty, megaphone-ish
/// voice. Output level is compensated so raising <see cref="Drive"/> changes the character rather
/// than just the volume.
/// </summary>
public sealed class DistortionEffect : AudioEffect
{
    SmoothedParam drive = new(8f);
    SmoothedParam mix = new(1f);

    /// <summary>Saturation amount. 1 is nearly clean, 50 is heavily squared off. Clamped to 1–50.</summary>
    public float Drive
    {
        get => this.drive.Target;
        set => this.drive.Target = Math.Clamp(value, 1f, 50f);
    }

    /// <summary>Wet/dry blend, 0–1. Default 1 (fully distorted).</summary>
    public float Mix
    {
        get => this.mix.Target;
        set => this.mix.Target = Math.Clamp(value, 0f, 1f);
    }

    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        this.drive.BeginBlock(samples.Length, sampleRate);
        this.mix.BeginBlock(samples.Length, sampleRate);

        for (var i = 0; i < samples.Length; i++)
        {
            var amount = this.drive.Next();
            var blend = this.mix.Next();

            float dry = samples[i];
            // Normalise, saturate, then divide by tanh(drive) so the curve fills the same headroom
            // at every drive setting instead of just getting louder.
            var wet = MathF.Tanh(dry / Dsp.FullScale * amount) / MathF.Tanh(amount) * Dsp.FullScale;

            samples[i] = Clip(dry + (wet - dry) * blend);
        }
    }

    public override void Reset()
    {
        this.drive.Snap();
        this.mix.Snap();
    }
}
