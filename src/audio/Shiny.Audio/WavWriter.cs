using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Shiny.Audio;

/// <summary>
/// Streaming RIFF/WAVE writer for uncompressed PCM. The header is written up front with placeholder
/// sizes and patched on <see cref="Finish"/> (or disposal), so a recording of any length streams
/// straight to disk instead of being buffered in memory.
/// </summary>
/// <remarks>
/// Not thread-safe — write from a single producer. If the destination stream cannot seek, the size
/// fields keep their placeholder values; most players fall back to the actual byte count, but prefer
/// a seekable stream when the file has to be exactly correct.
/// </remarks>
public sealed class WavWriter : IDisposable, IAsyncDisposable
{
    /// <summary>Size of the canonical RIFF/WAVE header this writer emits.</summary>
    public const int HeaderSize = 44;

    readonly Stream output;
    readonly bool leaveOpen;
    readonly long headerStart;
    readonly byte[] scratch = new byte[4096];
    long dataBytes;
    bool finished;

    /// <param name="output">Destination stream. Seekable streams get exact sizes patched in on close.</param>
    /// <param name="sampleRate">Samples per second. Defaults to the 16 kHz <see cref="IAudioSource"/> contract.</param>
    /// <param name="channels">Channel count. Defaults to mono, matching capture.</param>
    /// <param name="bitsPerSample">Bit depth. Only 8, 16, 24 and 32 are valid for PCM.</param>
    /// <param name="leaveOpen">Leave <paramref name="output"/> open when this writer is disposed.</param>
    public WavWriter(Stream output, int sampleRate = 16000, int channels = 1, int bitsPerSample = 16, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        if (bitsPerSample is not (8 or 16 or 24 or 32))
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), bitsPerSample, "PCM bit depth must be 8, 16, 24 or 32.");

        this.output = output;
        this.leaveOpen = leaveOpen;
        this.SampleRate = sampleRate;
        this.Channels = channels;
        this.BitsPerSample = bitsPerSample;

        this.headerStart = output.CanSeek ? output.Position : 0;

        Span<byte> header = stackalloc byte[HeaderSize];
        WriteHeader(header, sampleRate, channels, bitsPerSample, 0);
        output.Write(header);
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }

    /// <summary>Bytes of PCM written so far, excluding the header.</summary>
    public long DataLength => this.dataBytes;

    /// <summary>Duration of the audio written so far.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)this.dataBytes / (this.SampleRate * this.Channels * (this.BitsPerSample / 8)));

    /// <summary>Append raw PCM bytes in the format declared on the constructor.</summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(this.finished, this);
        if (pcm.IsEmpty)
            return;

        this.output.Write(pcm);
        this.dataBytes += pcm.Length;
    }

    /// <summary>
    /// Append 16-bit samples. Only valid when the writer was constructed with
    /// <c>bitsPerSample: 16</c> — the samples are written little-endian.
    /// </summary>
    public void Write(ReadOnlySpan<short> samples)
    {
        if (this.BitsPerSample != 16)
            throw new InvalidOperationException($"This writer is {this.BitsPerSample}-bit; the short overload only applies to 16-bit PCM.");

        if (BitConverter.IsLittleEndian)
        {
            this.Write(MemoryMarshal.AsBytes(samples));
            return;
        }

        // Big-endian hosts have to byte-swap; chunk through the scratch buffer to stay allocation-free.
        var perChunk = this.scratch.Length / 2;
        while (!samples.IsEmpty)
        {
            var take = Math.Min(perChunk, samples.Length);
            var dest = this.scratch.AsSpan(0, take * 2);
            for (var i = 0; i < take; i++)
                BinaryPrimitives.WriteInt16LittleEndian(dest[(i * 2)..], samples[i]);

            this.Write(dest);
            samples = samples[take..];
        }
    }

    /// <summary>
    /// Patch the RIFF and data chunk sizes and flush. Called automatically on disposal; safe to call
    /// more than once. After this the writer accepts no further data.
    /// </summary>
    public void Finish()
    {
        if (this.finished)
            return;

        this.finished = true;

        if (this.output.CanSeek)
        {
            var end = this.output.Position;
            var size = (int)Math.Min(this.dataBytes, int.MaxValue - HeaderSize);

            Span<byte> field = stackalloc byte[4];

            // RIFF chunk size = everything after the first 8 bytes.
            this.output.Position = this.headerStart + 4;
            BinaryPrimitives.WriteInt32LittleEndian(field, HeaderSize - 8 + size);
            this.output.Write(field);

            // data chunk size.
            this.output.Position = this.headerStart + 40;
            BinaryPrimitives.WriteInt32LittleEndian(field, size);
            this.output.Write(field);

            this.output.Position = end;
        }
        this.output.Flush();
    }

    public void Dispose()
    {
        this.Finish();
        if (!this.leaveOpen)
            this.output.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        this.Finish();
        if (!this.leaveOpen)
            await this.output.DisposeAsync();
    }

    /// <summary>
    /// Build a complete in-memory WAV file from a PCM buffer. Convenient for short clips (an upload
    /// body, a test fixture); use the streaming writer for recordings.
    /// </summary>
    public static byte[] CreateFile(ReadOnlySpan<byte> pcm, int sampleRate = 16000, int channels = 1, int bitsPerSample = 16)
    {
        var buffer = new byte[HeaderSize + pcm.Length];
        WriteHeader(buffer, sampleRate, channels, bitsPerSample, pcm.Length);
        pcm.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    /// <summary>Write a canonical 44-byte RIFF/WAVE PCM header into <paramref name="dest"/>.</summary>
    public static void WriteHeader(Span<byte> dest, int sampleRate, int channels, int bitsPerSample, int dataSize)
    {
        if (dest.Length < HeaderSize)
            throw new ArgumentException($"Header requires at least {HeaderSize} bytes.", nameof(dest));

        var blockAlign = channels * (bitsPerSample / 8);

        "RIFF"u8.CopyTo(dest[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(dest[4..8], HeaderSize - 8 + dataSize);
        "WAVE"u8.CopyTo(dest[8..12]);

        "fmt "u8.CopyTo(dest[12..16]);
        BinaryPrimitives.WriteInt32LittleEndian(dest[16..20], 16);          // PCM fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(dest[20..22], 1);           // format = PCM
        BinaryPrimitives.WriteInt16LittleEndian(dest[22..24], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(dest[24..28], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(dest[28..32], sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(dest[32..34], (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(dest[34..36], (short)bitsPerSample);

        "data"u8.CopyTo(dest[36..40]);
        BinaryPrimitives.WriteInt32LittleEndian(dest[40..44], dataSize);
    }
}
