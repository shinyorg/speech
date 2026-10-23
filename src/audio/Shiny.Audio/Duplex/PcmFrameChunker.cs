namespace Shiny.Audio.Duplex;

/// <summary>
/// Re-slices captured PCM into fixed-size frames. Platform capture hands back whatever buffer size the
/// hardware (or an AVAudioEngine tap) chooses; <see cref="IAudioDuplexSession.Captured"/> promises one
/// <see cref="AudioDuplexOptions.FrameDuration"/> frame per item.
/// </summary>
/// <remarks>Call from a single capture thread.</remarks>
sealed class PcmFrameChunker
{
    readonly byte[] pending;
    int filled;

    public PcmFrameChunker(int frameBytes)
    {
        if (frameBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameBytes));
        this.pending = new byte[frameBytes];
    }

    /// <summary>Bytes in one frame.</summary>
    public int FrameBytes => this.pending.Length;

    /// <summary>Bytes held back waiting for the rest of a frame.</summary>
    public int Pending => this.filled;

    /// <summary>
    /// Append captured PCM and hand every completed frame to <paramref name="emit"/>, as a new array the
    /// receiver owns. A partial frame is kept for the next call.
    /// </summary>
    public void Push(ReadOnlySpan<byte> pcm, Action<byte[]> emit)
    {
        while (!pcm.IsEmpty)
        {
            var take = Math.Min(pcm.Length, this.pending.Length - this.filled);
            pcm[..take].CopyTo(this.pending.AsSpan(this.filled));
            this.filled += take;
            pcm = pcm[take..];

            if (this.filled == this.pending.Length)
            {
                emit(this.pending.ToArray());
                this.filled = 0;
            }
        }
    }

    /// <summary>Drop a held-back partial frame.</summary>
    public void Reset() => this.filled = 0;
}
