namespace Shiny.Audio;

/// <summary>
/// The tail end of every capture pipeline: apply the effect chain, meter the result, and hand the
/// PCM to the consumer's stream.
/// </summary>
/// <remarks>
/// <para>
/// Each platform backend produces the same thing — a buffer of 16 kHz mono PCM16 — by a completely
/// different route (a tap callback, a blocking read loop, an audio-graph quantum, a JS interop
/// callback). Everything downstream of that is identical, so it lives here once instead of five
/// times.
/// </para>
/// <para>
/// Effects run <b>before</b> metering, so <see cref="IAudioSource.InputLevelChanged"/> reports the
/// processed signal — the level a user sees matches what they will hear.
/// </para>
/// <para>Call <see cref="Write"/> from a single capture thread.</para>
/// </remarks>
sealed class CaptureSink : IDisposable
{
    readonly PipeStream pipe = new();
    readonly AudioLevelThrottle throttle = new();
    readonly AudioEffectChain? effects;
    readonly int sampleRate;
    readonly Action<double>? onLevel;

    public CaptureSink(AudioCaptureOptions? options, Action<double>? onLevel, int sampleRate = 16000)
    {
        this.effects = options?.Effects;
        this.onLevel = onLevel;
        this.sampleRate = sampleRate;

        // Start from a clean slate: a chain reused across sessions must not replay the previous
        // take's reverb tail into the first buffer of this one.
        this.effects?.Reset();
    }

    /// <summary>The stream handed back to the caller of <c>StartCaptureAsync</c>.</summary>
    public Stream Stream => this.pipe;

    /// <summary>
    /// Process, meter and forward one captured buffer. Returns false once the consumer has gone
    /// away, which is the signal for a polling backend to stop reading.
    /// </summary>
    public bool Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0)
            return true;

        try
        {
            this.effects?.Process(buffer.AsSpan(offset, count), this.sampleRate);

            if (this.onLevel != null && this.throttle.TryEmit(AudioLevel.FromPcm16(buffer.AsSpan(offset, count)), out var level))
                this.onLevel(level);

            this.pipe.Write(buffer, offset, count);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // The pipe writer was completed by Dispose on the stop path.
            return false;
        }
    }

    public void Dispose() => this.pipe.Dispose();
}
