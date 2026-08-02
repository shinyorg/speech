namespace Shiny.Audio;

/// <summary>
/// A float parameter that can be assigned from any thread and is ramped toward its new value across
/// the audio buffer instead of jumping.
/// </summary>
/// <remarks>
/// <para>
/// Two properties make this safe in a capture callback. First, the target is a single <c>float</c>:
/// 32-bit writes are atomic on every runtime we target, so no lock is needed and the audio thread
/// never blocks. (A <c>double</c> would not do — its write can tear on 32-bit ARM.) Second, the
/// value is interpolated rather than applied instantly: stepping a gain or mix straight from one
/// value to another puts a discontinuity in the waveform, which is audible as a click every time a
/// slider moves.
/// </para>
/// <para>
/// Ramp length is capped at <see cref="SmoothingMs"/> so behaviour does not depend on the platform's
/// buffer size, which varies from a few milliseconds to well over a hundred.
/// </para>
/// </remarks>
public struct SmoothedParam
{
    /// <summary>Time taken to travel to a newly assigned value.</summary>
    public const float SmoothingMs = 15f;

    float target;
    float current;
    float blockTarget;
    float step;

    public SmoothedParam(float initial)
    {
        this.target = initial;
        this.current = initial;
        this.blockTarget = initial;
        this.step = 0f;
    }

    /// <summary>
    /// The value being ramped toward. Safe to assign from any thread at any time.
    /// </summary>
    public float Target
    {
        get => Volatile.Read(ref this.target);
        set => Volatile.Write(ref this.target, value);
    }

    /// <summary>The instantaneous smoothed value. Only meaningful on the audio thread.</summary>
    public readonly float Current => this.current;

    /// <summary>True when the ramp has caught up and per-sample interpolation can be skipped.</summary>
    public readonly bool IsSteady => this.current == this.blockTarget && this.step == 0f;

    /// <summary>
    /// Latch the target for this buffer and compute the per-sample increment. Call once at the top
    /// of <c>Process</c>, before the sample loop.
    /// </summary>
    public void BeginBlock(int sampleCount, int sampleRate)
    {
        this.blockTarget = Volatile.Read(ref this.target);

        if (this.current == this.blockTarget || sampleCount <= 0)
        {
            this.step = 0f;
            return;
        }

        var rampSamples = Math.Max(1, (int)(sampleRate * SmoothingMs / 1000f));
        if (rampSamples > sampleCount)
            rampSamples = sampleCount;

        this.step = (this.blockTarget - this.current) / rampSamples;
    }

    /// <summary>Advance one sample and return the value to use for it.</summary>
    public float Next()
    {
        if (this.step == 0f)
            return this.current;

        this.current += this.step;

        // Stop exactly on the target rather than oscillating around it on the last step.
        if ((this.step > 0f && this.current >= this.blockTarget) ||
            (this.step < 0f && this.current <= this.blockTarget))
        {
            this.current = this.blockTarget;
            this.step = 0f;
        }
        return this.current;
    }

    /// <summary>Jump straight to the target with no ramp — used when a session (re)starts.</summary>
    public void Snap()
    {
        this.current = Volatile.Read(ref this.target);
        this.blockTarget = this.current;
        this.step = 0f;
    }
}
