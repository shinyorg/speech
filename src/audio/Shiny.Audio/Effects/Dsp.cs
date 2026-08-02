namespace Shiny.Audio;

/// <summary>Small shared primitives used by the built-in effects.</summary>
static class Dsp
{
    /// <summary>Full-scale magnitude of a PCM16 sample.</summary>
    public const float FullScale = 32768f;

    public static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>
    /// Saturating soft clip. Leaves the quiet majority of a signal alone and rounds off peaks
    /// instead of squaring them the way a hard clamp does, so pushing gain sounds like compression
    /// rather than crackle.
    /// </summary>
    public static float SoftClip(float sample)
    {
        const float knee = 0.7f * FullScale;
        var magnitude = MathF.Abs(sample);
        if (magnitude <= knee)
            return sample;

        var sign = MathF.Sign(sample);
        var over = (magnitude - knee) / (FullScale - knee);
        // tanh maps the remaining headroom asymptotically, so the output can never exceed full scale.
        return sign * (knee + (FullScale - knee) * MathF.Tanh(over));
    }

    /// <summary>
    /// One-pole coefficient for an envelope that reaches ~63% of a step in the given time.
    /// </summary>
    public static float TimeConstant(float milliseconds, int sampleRate)
    {
        if (milliseconds <= 0f)
            return 0f;

        return MathF.Exp(-1f / (milliseconds * 0.001f * sampleRate));
    }
}

/// <summary>
/// A one-pole smoother evaluated once per buffer rather than per sample.
/// </summary>
/// <remarks>
/// Some parameters (filter cutoff, pitch ratio) feed an expensive recalculation — coefficients, a
/// <c>Pow</c> — that is wasteful to redo per sample. Moving them a fraction of the way toward the
/// target each buffer still removes the zipper noise of a dragged slider, at block rate.
/// </remarks>
struct BlockSmoother(float initial)
{
    const float Rate = 0.35f;

    float current = initial;

    public readonly float Current => this.current;

    /// <summary>Step toward <paramref name="target"/> and return the value to use for this buffer.</summary>
    public float Next(float target)
    {
        var delta = target - this.current;

        // Snap when close enough, so the exponential tail does not leave the value permanently off.
        if (MathF.Abs(delta) < 1e-4f * MathF.Max(1f, MathF.Abs(target)))
            this.current = target;
        else
            this.current += delta * Rate;

        return this.current;
    }

    public void Snap(float value) => this.current = value;
}

/// <summary>
/// Circular delay line with fractional (linearly interpolated) reads, shared by the echo and chorus
/// effects. Sized once for the longest delay it will ever be asked for, so changing a delay time at
/// runtime moves a read index and never allocates on the audio thread.
/// </summary>
sealed class DelayLine
{
    float[] buffer;
    int writeIndex;

    public DelayLine(int capacity)
    {
        this.buffer = new float[Math.Max(2, capacity)];
    }

    public int Capacity => this.buffer.Length;

    /// <summary>Grow the line if the sample rate turned out to need more room. Not real-time safe.</summary>
    public void EnsureCapacity(int capacity)
    {
        if (capacity <= this.buffer.Length)
            return;

        this.buffer = new float[capacity];
        this.writeIndex = 0;
    }

    public void Write(float sample)
    {
        this.buffer[this.writeIndex] = sample;
        if (++this.writeIndex == this.buffer.Length)
            this.writeIndex = 0;
    }

    /// <summary>
    /// Read the sample written <paramref name="delaySamples"/> steps ago, interpolating between
    /// neighbours for fractional delays.
    /// </summary>
    /// <remarks>
    /// The delay is measured from the slot about to be written, so this must be called <b>before</b>
    /// <see cref="Write"/> for the current sample. A delay of <c>n</c> then returns exactly the
    /// sample handed in <c>n</c> calls earlier — which is what makes an echo land on its nominal
    /// time rather than one sample late.
    /// </remarks>
    public float Read(float delaySamples)
    {
        var length = this.buffer.Length;
        delaySamples = Math.Clamp(delaySamples, 0f, length - 2f);

        // The whole and fractional parts are separated before any wrapping, and the wrap is done on
        // integers. Wrapping a float position instead can round a value just below zero up to
        // exactly the buffer length, which indexes off the end.
        var whole = (int)MathF.Floor(delaySamples);
        var frac = delaySamples - whole;

        var recent = this.writeIndex - whole;
        if (recent < 0)
            recent += length;
        if (recent >= length)
            recent -= length;

        // One more sample back in time — the other side of the interpolation.
        var older = recent - 1;
        if (older < 0)
            older += length;

        return this.buffer[recent] + (this.buffer[older] - this.buffer[recent]) * frac;
    }

    public void Clear()
    {
        Array.Clear(this.buffer);
        this.writeIndex = 0;
    }
}
