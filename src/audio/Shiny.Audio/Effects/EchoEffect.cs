namespace Shiny.Audio;

/// <summary>
/// Delay/echo — repeats the signal after <see cref="DelayMs"/>, each repeat quieter than the last by
/// <see cref="Feedback"/>.
/// </summary>
/// <remarks>
/// The delay line is allocated once for <see cref="MaxDelayMs"/>, so moving the delay time while
/// recording only moves a read index — it never reallocates on the capture thread.
/// </remarks>
public sealed class EchoEffect : AudioEffect
{
    /// <summary>Longest delay the line is sized for.</summary>
    public const float MaxDelayMs = 2000f;

    SmoothedParam delayMs = new(250f);
    SmoothedParam feedback = new(0.35f);
    SmoothedParam mix = new(0.35f);

    DelayLine? line;

    /// <summary>Time between repeats, in milliseconds. Clamped to 1–<see cref="MaxDelayMs"/>. Default 250.</summary>
    public float DelayMs
    {
        get => this.delayMs.Target;
        set => this.delayMs.Target = Math.Clamp(value, 1f, MaxDelayMs);
    }

    /// <summary>
    /// How much of each repeat is fed back in. Clamped to 0–0.95 — at 1.0 the echo would build
    /// without limit and saturate. Default 0.35.
    /// </summary>
    public float Feedback
    {
        get => this.feedback.Target;
        set => this.feedback.Target = Math.Clamp(value, 0f, 0.95f);
    }

    /// <summary>Wet/dry blend, 0–1. Default 0.35.</summary>
    public float Mix
    {
        get => this.mix.Target;
        set => this.mix.Target = Math.Clamp(value, 0f, 1f);
    }

    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        var capacity = (int)(MaxDelayMs * 0.001f * sampleRate) + 4;
        if (this.line == null)
            this.line = new DelayLine(capacity);
        else
            this.line.EnsureCapacity(capacity);

        this.delayMs.BeginBlock(samples.Length, sampleRate);
        this.feedback.BeginBlock(samples.Length, sampleRate);
        this.mix.BeginBlock(samples.Length, sampleRate);

        for (var i = 0; i < samples.Length; i++)
        {
            var delaySamples = this.delayMs.Next() * 0.001f * sampleRate;
            var delayed = this.line.Read(delaySamples);

            float dry = samples[i];
            this.line.Write(dry + delayed * this.feedback.Next());

            samples[i] = Clip(dry + (delayed - dry) * this.mix.Next());
        }
    }

    public override void Reset()
    {
        this.line?.Clear();
        this.delayMs.Snap();
        this.feedback.Snap();
        this.mix.Snap();
    }
}
