namespace Shiny.Audio;

/// <summary>Response shape for <see cref="BiquadFilterEffect"/>.</summary>
public enum BiquadFilterType
{
    /// <summary>Passes everything below the cutoff.</summary>
    LowPass,

    /// <summary>Passes everything above the cutoff — removes rumble and handling noise.</summary>
    HighPass,

    /// <summary>Passes a band around the centre frequency.</summary>
    BandPass,

    /// <summary>Removes a narrow band — e.g. 50/60 Hz mains hum.</summary>
    Notch
}

/// <summary>
/// A second-order (biquad) filter using the standard RBJ audio EQ cookbook coefficients.
/// </summary>
/// <remarks>
/// Coefficients are recomputed once per buffer from a smoothed cutoff rather than per sample: the
/// trigonometry is far too expensive to run per sample, and updating at buffer rate is enough to
/// keep a dragged cutoff slider free of zipper noise.
/// </remarks>
public sealed class BiquadFilterEffect : AudioEffect
{
    int type = (int)BiquadFilterType.LowPass;
    float frequency = 1000f;
    float q = 0.707f;

    BlockSmoother smoothedFrequency = new(1000f);

    float a1, a2, b0, b1, b2;
    float x1, x2, y1, y2;
    float lastFrequency = -1f, lastQ = -1f;
    int lastType = -1;
    int lastRate = -1;

    /// <summary>Filter shape. Default <see cref="BiquadFilterType.LowPass"/>.</summary>
    public BiquadFilterType Type
    {
        get => (BiquadFilterType)Volatile.Read(ref this.type);
        set => Volatile.Write(ref this.type, (int)value);
    }

    /// <summary>Cutoff (or centre) frequency in Hz. Clamped to 20 Hz–Nyquist. Default 1000.</summary>
    public float Frequency
    {
        get => Volatile.Read(ref this.frequency);
        set => Volatile.Write(ref this.frequency, MathF.Max(20f, value));
    }

    /// <summary>
    /// Resonance. 0.707 is maximally flat; higher values peak at the cutoff. Clamped to 0.1–20.
    /// </summary>
    public float Q
    {
        get => Volatile.Read(ref this.q);
        set => Volatile.Write(ref this.q, Math.Clamp(value, 0.1f, 20f));
    }

    /// <summary>A 300 Hz–3.4 kHz band-pass — the classic telephone/walkie-talkie voice.</summary>
    public static BiquadFilterEffect Telephone() => new()
    {
        Type = BiquadFilterType.BandPass,
        Frequency = 1200f,
        Q = 0.8f
    };

    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        var nyquist = sampleRate * 0.5f;
        var target = MathF.Min(this.Frequency, nyquist * 0.98f);
        var cutoff = this.smoothedFrequency.Next(target);
        this.UpdateCoefficients(cutoff, this.Q, this.Type, sampleRate);

        for (var i = 0; i < samples.Length; i++)
        {
            float x = samples[i];
            var y = this.b0 * x + this.b1 * this.x1 + this.b2 * this.x2
                    - this.a1 * this.y1 - this.a2 * this.y2;

            this.x2 = this.x1;
            this.x1 = x;
            this.y2 = this.y1;
            this.y1 = y;

            samples[i] = Clip(y);
        }
    }

    void UpdateCoefficients(float cutoff, float resonance, BiquadFilterType filterType, int sampleRate)
    {
        if (cutoff == this.lastFrequency && resonance == this.lastQ &&
            (int)filterType == this.lastType && sampleRate == this.lastRate)
            return;

        this.lastFrequency = cutoff;
        this.lastQ = resonance;
        this.lastType = (int)filterType;
        this.lastRate = sampleRate;

        var omega = 2f * MathF.PI * cutoff / sampleRate;
        var sin = MathF.Sin(omega);
        var cos = MathF.Cos(omega);
        var alpha = sin / (2f * resonance);

        float a0, na1, na2, nb0, nb1, nb2;

        switch (filterType)
        {
            case BiquadFilterType.HighPass:
                nb0 = (1f + cos) / 2f;
                nb1 = -(1f + cos);
                nb2 = (1f + cos) / 2f;
                a0 = 1f + alpha;
                na1 = -2f * cos;
                na2 = 1f - alpha;
                break;

            case BiquadFilterType.BandPass:
                // Constant 0 dB peak gain.
                nb0 = alpha;
                nb1 = 0f;
                nb2 = -alpha;
                a0 = 1f + alpha;
                na1 = -2f * cos;
                na2 = 1f - alpha;
                break;

            case BiquadFilterType.Notch:
                nb0 = 1f;
                nb1 = -2f * cos;
                nb2 = 1f;
                a0 = 1f + alpha;
                na1 = -2f * cos;
                na2 = 1f - alpha;
                break;

            default: // LowPass
                nb0 = (1f - cos) / 2f;
                nb1 = 1f - cos;
                nb2 = (1f - cos) / 2f;
                a0 = 1f + alpha;
                na1 = -2f * cos;
                na2 = 1f - alpha;
                break;
        }

        this.b0 = nb0 / a0;
        this.b1 = nb1 / a0;
        this.b2 = nb2 / a0;
        this.a1 = na1 / a0;
        this.a2 = na2 / a0;
    }

    public override void Reset()
    {
        this.x1 = this.x2 = this.y1 = this.y2 = 0f;
        this.smoothedFrequency.Snap(this.Frequency);
    }
}
