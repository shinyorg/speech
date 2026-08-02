namespace Shiny.Audio;

/// <summary>
/// Silences the signal while it sits below a threshold — room tone, fan noise, handling rumble —
/// and opens as soon as someone speaks.
/// </summary>
/// <remarks>
/// Useful ahead of <see cref="ReverbEffect"/> or <see cref="EchoEffect"/>, which otherwise feed
/// background noise into the tail and smear it across the whole recording.
/// </remarks>
public sealed class NoiseGateEffect : AudioEffect
{
    float thresholdDb = -45f;
    float attackMs = 5f;
    float releaseMs = 150f;

    float envelope;
    float gateGain;

    /// <summary>Level below which the gate closes, in dBFS. Clamped to −90…0. Default −45.</summary>
    public float ThresholdDb
    {
        get => Volatile.Read(ref this.thresholdDb);
        set => Volatile.Write(ref this.thresholdDb, Math.Clamp(value, -90f, 0f));
    }

    /// <summary>How quickly the gate opens, in milliseconds. Clamped to 0.1–200. Default 5.</summary>
    public float AttackMs
    {
        get => Volatile.Read(ref this.attackMs);
        set => Volatile.Write(ref this.attackMs, Math.Clamp(value, 0.1f, 200f));
    }

    /// <summary>
    /// How quickly the gate closes, in milliseconds. Clamped to 1–2000. Default 150 — long enough
    /// that it does not chop the ends off words.
    /// </summary>
    public float ReleaseMs
    {
        get => Volatile.Read(ref this.releaseMs);
        set => Volatile.Write(ref this.releaseMs, Math.Clamp(value, 1f, 2000f));
    }

    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        var threshold = Dsp.DbToLinear(this.ThresholdDb) * Dsp.FullScale;
        var envelopeDecay = Dsp.TimeConstant(20f, sampleRate);
        var attack = Dsp.TimeConstant(this.AttackMs, sampleRate);
        var release = Dsp.TimeConstant(this.ReleaseMs, sampleRate);

        for (var i = 0; i < samples.Length; i++)
        {
            var magnitude = MathF.Abs(samples[i]);

            // Peak follower: jump to a new peak instantly, decay slowly, so a gap between syllables
            // does not slam the gate shut mid-word.
            this.envelope = magnitude > this.envelope
                ? magnitude
                : magnitude + (this.envelope - magnitude) * envelopeDecay;

            var target = this.envelope >= threshold ? 1f : 0f;
            var coefficient = target > this.gateGain ? attack : release;
            this.gateGain = target + (this.gateGain - target) * coefficient;

            samples[i] = Clip(samples[i] * this.gateGain);
        }
    }

    public override void Reset()
    {
        this.envelope = 0f;
        this.gateGain = 0f;
    }
}
