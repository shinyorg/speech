namespace Shiny.Audio;

/// <summary>
/// Room reverb using the Freeverb topology — eight parallel damped comb filters feeding four
/// allpass filters in series.
/// </summary>
/// <remarks>
/// <para>
/// The classic tunings are specified at 44.1 kHz, so the line lengths are scaled to whatever sample
/// rate the session runs at; at the 16 kHz capture rate they shorten proportionally and the decay
/// character is preserved.
/// </para>
/// <para>
/// Reverb on a narrowband mono mic sounds thinner than on full-bandwidth stereo — this is a voice
/// effect, not a mastering tool.
/// </para>
/// </remarks>
public sealed class ReverbEffect : AudioEffect
{
    // Freeverb's original tunings, in samples at 44.1 kHz.
    const int TuningRate = 44100;
    static readonly int[] CombTuning = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    static readonly int[] AllpassTuning = [556, 441, 341, 225];

    readonly Comb[] combs = new Comb[CombTuning.Length];
    readonly Allpass[] allpasses = new Allpass[AllpassTuning.Length];

    SmoothedParam mix = new(0.35f);
    float roomSize = 0.6f;
    float damping = 0.4f;
    int builtRate;

    /// <summary>
    /// Decay length, 0–1. Low values give a small tiled room, high values a cathedral. Default 0.6.
    /// </summary>
    public float RoomSize
    {
        get => Volatile.Read(ref this.roomSize);
        set => Volatile.Write(ref this.roomSize, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>
    /// High-frequency absorption, 0–1. Higher values make the tail darker, as soft furnishings do.
    /// Default 0.4.
    /// </summary>
    public float Damping
    {
        get => Volatile.Read(ref this.damping);
        set => Volatile.Write(ref this.damping, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>Wet/dry blend, 0–1. Default 0.35.</summary>
    public float Mix
    {
        get => this.mix.Target;
        set => this.mix.Target = Math.Clamp(value, 0f, 1f);
    }

    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        this.Build(sampleRate);
        this.mix.BeginBlock(samples.Length, sampleRate);

        // Freeverb's feedback/damping mapping, scaled from its 0–1 controls.
        var feedback = 0.7f + this.RoomSize * 0.28f;
        var damp = this.Damping * 0.4f;

        for (var i = 0; i < samples.Length; i++)
        {
            float dry = samples[i];

            // Freeverb's fixed input gain. Eight combs each with a DC gain of 1/(1 - feedback) sum
            // to a large number, and this is what scales the result back to roughly unity — so the
            // wet signal stays in PCM16 units and needs no further scaling.
            var input = dry * 0.015f;

            var wet = 0f;
            for (var c = 0; c < this.combs.Length; c++)
                wet += this.combs[c].Process(input, feedback, damp);

            for (var a = 0; a < this.allpasses.Length; a++)
                wet = this.allpasses[a].Process(wet);

            samples[i] = Clip(dry + (wet - dry) * this.mix.Next());
        }
    }

    void Build(int sampleRate)
    {
        if (this.builtRate == sampleRate)
            return;

        var scale = (float)sampleRate / TuningRate;

        for (var i = 0; i < CombTuning.Length; i++)
            this.combs[i] = new Comb(Math.Max(4, (int)(CombTuning[i] * scale)));

        for (var i = 0; i < AllpassTuning.Length; i++)
            this.allpasses[i] = new Allpass(Math.Max(4, (int)(AllpassTuning[i] * scale)));

        this.builtRate = sampleRate;
    }

    public override void Reset()
    {
        for (var i = 0; i < this.combs.Length; i++)
            this.combs[i]?.Clear();

        for (var i = 0; i < this.allpasses.Length; i++)
            this.allpasses[i]?.Clear();

        this.mix.Snap();
    }

    /// <summary>Lowpass-damped comb filter — one of the eight parallel decay taps.</summary>
    sealed class Comb(int size)
    {
        readonly float[] buffer = new float[size];
        int index;
        float filterStore;

        public float Process(float input, float feedback, float damp)
        {
            var output = this.buffer[this.index];

            // One-pole lowpass inside the feedback loop: this is what makes the tail lose treble
            // as it decays instead of ringing brightly forever.
            this.filterStore = output * (1f - damp) + this.filterStore * damp;

            this.buffer[this.index] = input + this.filterStore * feedback;
            if (++this.index == this.buffer.Length)
                this.index = 0;

            return output;
        }

        public void Clear()
        {
            Array.Clear(this.buffer);
            this.index = 0;
            this.filterStore = 0f;
        }
    }

    /// <summary>Allpass diffuser — smears the comb output so individual repeats stop being audible.</summary>
    sealed class Allpass(int size)
    {
        const float Feedback = 0.5f;

        readonly float[] buffer = new float[size];
        int index;

        public float Process(float input)
        {
            var buffered = this.buffer[this.index];
            var output = -input + buffered;

            this.buffer[this.index] = input + buffered * Feedback;
            if (++this.index == this.buffer.Length)
                this.index = 0;

            return output;
        }

        public void Clear()
        {
            Array.Clear(this.buffer);
            this.index = 0;
        }
    }
}
