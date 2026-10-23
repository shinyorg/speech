namespace Shiny.Audio.Duplex;

/// <summary>
/// The playback queue behind <see cref="IAudioDuplexSession.Play"/>: network audio is written in
/// whatever sizes it arrives, and the platform output pulls fixed-size buffers at the hardware rate.
/// </summary>
/// <remarks>
/// <para>
/// It smooths network jitter with three rules:
/// <list type="bullet">
/// <item><b>Priming</b>: nothing plays until <c>target</c> bytes are queued, so the first late packet
/// doesn't immediately starve the output.</item>
/// <item><b>Underrun</b>: when a read finds less than it asked for, what there is plays, the rest is
/// silence, and the buffer primes again before playing more.</item>
/// <item><b>Overflow</b>: when a write would take the queue past <c>max</c>, the oldest audio is
/// dropped back down to <c>target</c>, so a burst can't leave a permanent delay behind it.</item>
/// </list>
/// </para>
/// <para>PCM16: every length is kept to whole samples. Thread-safe — one writer and one reader, or more.</para>
/// </remarks>
sealed class AudioJitterBuffer
{
    readonly object gate = new();
    readonly byte[] ring;
    readonly int bytesPerSecond;
    readonly int targetBytes;
    readonly int maxBytes;
    int head;    // index of the oldest queued byte
    int count;   // queued bytes
    bool playing;

    /// <param name="sampleRate">PCM16 mono sample rate.</param>
    /// <param name="target">Queue this much before playing (and again after an underrun).</param>
    /// <param name="max">Never queue more than this; must be greater than <paramref name="target"/>.</param>
    public AudioJitterBuffer(int sampleRate, TimeSpan target, TimeSpan max)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (target < TimeSpan.Zero || max <= target)
            throw new ArgumentException("max must be greater than target, and target can't be negative.");

        this.bytesPerSecond = sampleRate * 2;
        this.targetBytes = ToBytes(target);
        this.maxBytes = Math.Max(ToBytes(max), 2);
        this.ring = new byte[this.maxBytes];
    }

    /// <summary>Queued audio.</summary>
    public TimeSpan Buffered
    {
        get
        {
            lock (this.gate)
                return TimeSpan.FromSeconds((double)this.count / this.bytesPerSecond);
        }
    }

    /// <summary>Queued bytes.</summary>
    public int BufferedBytes
    {
        get { lock (this.gate) return this.count; }
    }

    /// <summary>True once primed and until the next underrun.</summary>
    public bool IsPlaying
    {
        get { lock (this.gate) return this.playing; }
    }

    /// <summary>Times playback ran dry after it had started.</summary>
    public int Underruns { get; private set; }

    /// <summary>Bytes discarded by the overflow rule.</summary>
    public long DroppedBytes { get; private set; }

    /// <summary>Queue PCM16 for playback. An odd trailing byte is ignored.</summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        pcm = pcm[..(pcm.Length & ~1)];
        if (pcm.IsEmpty)
            return;

        lock (this.gate)
        {
            if (this.count + pcm.Length > this.maxBytes)
            {
                // Overflow: keep only the newest `keep` bytes across what's queued plus this write. Back to
                // the target, but never less than this write, so a zero target still keeps the latest audio.
                var keep = Math.Min(Math.Max(this.targetBytes, pcm.Length), this.maxBytes);
                var excess = this.count + pcm.Length - keep;
                this.DroppedBytes += excess;

                var fromQueue = Math.Min(excess, this.count);
                this.head = (this.head + fromQueue) % this.ring.Length;
                this.count -= fromQueue;

                // A single write larger than everything we may hold: only its newest part survives.
                pcm = pcm[(excess - fromQueue)..];
            }

            var tail = (this.head + this.count) % this.ring.Length;
            var first = Math.Min(pcm.Length, this.ring.Length - tail);
            pcm[..first].CopyTo(this.ring.AsSpan(tail));
            pcm[first..].CopyTo(this.ring);
            this.count += pcm.Length;
        }
    }

    /// <summary>
    /// Fill <paramref name="destination"/> with the next audio to play. Silence fills whatever the queue
    /// can't (still priming, or ran dry).
    /// </summary>
    /// <returns>Bytes of real audio written; the rest of the buffer is silence.</returns>
    public int Read(Span<byte> destination)
    {
        lock (this.gate)
        {
            if (!this.playing)
            {
                if (this.count == 0 || this.count < this.targetBytes)
                {
                    destination.Clear();
                    return 0;
                }
                this.playing = true;
            }

            var n = Math.Min(this.count, destination.Length) & ~1;
            var first = Math.Min(n, this.ring.Length - this.head);
            this.ring.AsSpan(this.head, first).CopyTo(destination);
            this.ring.AsSpan(0, n - first).CopyTo(destination[first..]);
            this.head = (this.head + n) % this.ring.Length;
            this.count -= n;

            if (n < destination.Length)
            {
                // Ran dry mid-buffer: silence for the rest, and re-prime before playing again.
                destination[n..].Clear();
                this.playing = false;
                this.Underruns++;
            }
            if (this.count == 0)
                this.head = 0;

            return n;
        }
    }

    /// <summary>Discard everything queued and go back to priming.</summary>
    public void Clear()
    {
        lock (this.gate)
        {
            this.head = 0;
            this.count = 0;
            this.playing = false;
        }
    }

    int ToBytes(TimeSpan duration)
        => (int)(duration.TotalSeconds * this.bytesPerSecond) & ~1;
}
