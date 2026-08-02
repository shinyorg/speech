namespace Shiny.Audio;

/// <summary>
/// Shifts pitch up or down without changing how long the audio lasts — the voice-changer effect.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it works.</b> Input is written into a delay line at normal speed while two read heads,
/// half a window apart, sweep through it at the pitch ratio. Reading faster than writing raises the
/// pitch; reading slower lowers it. Each head is faded in and out as it wraps, so the seam where it
/// jumps back is masked by the other head — that crossfade is what keeps the duration identical to
/// the input while the pitch changes.
/// </para>
/// <para>
/// <b>Cost.</b> This is the only built-in effect that adds meaningful latency — up to one window
/// (<see cref="WindowMs"/>, about 50 ms). Large shifts also introduce some warble on sustained
/// tones; that is inherent to time-domain shifting and is least noticeable on speech, which is what
/// this is for. Beyond roughly ±7 semitones a voice starts to sound obviously processed.
/// </para>
/// </remarks>
public sealed class PitchShiftEffect : AudioEffect
{
    /// <summary>Crossfade window length. Longer is smoother but adds latency.</summary>
    public const float WindowMs = 50f;

    float semitones;
    BlockSmoother smoothedSemitones = new(0f);

    DelayLine? line;
    float position;
    int windowSamples;
    int builtRate;

    /// <summary>
    /// Shift in semitones. Positive is higher, negative is lower, 0 is a clean passthrough.
    /// Clamped to ±24 (two octaves). Default 0.
    /// </summary>
    public float Semitones
    {
        get => Volatile.Read(ref this.semitones);
        set => Volatile.Write(ref this.semitones, Math.Clamp(value, -24f, 24f));
    }

    /// <summary>The current shift expressed as a frequency multiplier — 2.0 is one octave up.</summary>
    public float Ratio => MathF.Pow(2f, this.Semitones / 12f);

    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        this.Build(sampleRate);

        var ratio = MathF.Pow(2f, this.smoothedSemitones.Next(this.Semitones) / 12f);
        var window = this.windowSamples;

        for (var i = 0; i < samples.Length; i++)
        {
            float dry = samples[i];

            // A ratio of 1 has no seam to hide, and running the crossfade anyway would comb-filter
            // the signal against itself. Pass it straight through.
            if (MathF.Abs(ratio - 1f) < 1e-4f)
            {
                this.line!.Write(dry);
                continue;
            }

            var second = this.position + window * 0.5f;
            if (second >= window)
                second -= window;

            // Read before writing (the delay line measures from the pending write slot), so the
            // heads are offset by one sample from "now" — inaudible, and it keeps both taps on the
            // same convention the echo and chorus use.
            var a = this.line!.Read(this.position + 1f);
            var b = this.line.Read(second + 1f);
            this.line.Write(dry);

            // Triangular crossfade: head A is silent as it wraps at 0 and at the end of the window,
            // and loudest halfway through — exactly where head B is wrapping.
            var weightA = 1f - MathF.Abs(2f * this.position / window - 1f);

            samples[i] = Clip(a * weightA + b * (1f - weightA));

            // Read heads advance at `ratio`, the write head at 1, so the gap between them grows by
            // (1 - ratio) each sample.
            this.position += 1f - ratio;
            if (this.position >= window)
                this.position -= window;
            else if (this.position < 0f)
                this.position += window;
        }
    }

    void Build(int sampleRate)
    {
        if (this.builtRate == sampleRate && this.line != null)
            return;

        this.windowSamples = Math.Max(16, (int)(WindowMs * 0.001f * sampleRate));
        this.line = new DelayLine(this.windowSamples + 4);
        this.position = 0f;
        this.builtRate = sampleRate;
    }

    public override void Reset()
    {
        this.line?.Clear();
        this.position = 0f;
        this.smoothedSemitones.Snap(this.Semitones);
    }
}
