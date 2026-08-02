using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Shiny.Audio;

/// <summary>
/// Streaming reader for uncompressed RIFF/WAVE PCM — the counterpart to <see cref="WavWriter"/>.
/// Reads the format up front and then hands out the data chunk a buffer at a time, so a long file
/// never has to be materialized in memory.
/// </summary>
/// <remarks>
/// The chunk list is walked rather than assuming the canonical 44-byte header, because real files
/// carry <c>LIST</c>/<c>fact</c> chunks ahead of the data.
/// </remarks>
public sealed class WavReader : IDisposable, IAsyncDisposable
{
    const int FormatPcm = 1;
    const int FormatExtensible = 0xFFFE;

    readonly Stream input;
    readonly bool leaveOpen;
    long remaining;

    /// <param name="input">Source stream, positioned at the start of the RIFF header.</param>
    /// <param name="leaveOpen">Leave <paramref name="input"/> open when this reader is disposed.</param>
    /// <exception cref="NotSupportedException">The stream is not PCM WAV, or has no data chunk.</exception>
    public WavReader(Stream input, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        this.input = input;
        this.leaveOpen = leaveOpen;

        Span<byte> riff = stackalloc byte[12];
        ReadExactly(input, riff);
        if (!riff[..4].SequenceEqual("RIFF"u8) || !riff[8..12].SequenceEqual("WAVE"u8))
            throw new NotSupportedException("Stream is not a RIFF/WAVE file.");

        var formatTag = 0;
        Span<byte> header = stackalloc byte[8];

        while (true)
        {
            if (!TryReadExactly(input, header))
                throw new NotSupportedException("WAV stream contains no data chunk.");

            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);

            if (header[..4].SequenceEqual("fmt "u8))
            {
                if (chunkSize < 16)
                    throw new NotSupportedException("WAV format chunk is truncated.");

                var fmt = new byte[chunkSize];
                ReadExactly(input, fmt);

                formatTag = BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(0, 2));
                this.Channels = BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(2, 2));
                this.SampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4, 4));
                this.BitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(14, 2));

                if (formatTag is not (FormatPcm or FormatExtensible))
                    throw new NotSupportedException($"Only uncompressed PCM is supported (format tag {formatTag}).");

                if ((chunkSize & 1) == 1)
                    Skip(input, 1);   // chunks are word-aligned
            }
            else if (header[..4].SequenceEqual("data"u8))
            {
                if (formatTag == 0)
                    throw new NotSupportedException("WAV data chunk appeared before its format chunk.");

                // A streaming writer that could not seek leaves a placeholder size; fall back to
                // "read until the stream ends" rather than returning nothing.
                this.DataLength = chunkSize > 0 ? chunkSize : long.MaxValue;
                if (chunkSize > 0 && input.CanSeek)
                    this.DataLength = Math.Min(chunkSize, input.Length - input.Position);

                this.remaining = this.DataLength;
                return;
            }
            else
            {
                Skip(input, chunkSize + (chunkSize & 1));
            }
        }
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }

    /// <summary>Length of the data chunk in bytes.</summary>
    public long DataLength { get; }

    /// <summary>Duration of the file.</summary>
    public TimeSpan Duration => this.DataLength == long.MaxValue
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds((double)this.DataLength / (this.SampleRate * this.Channels * (this.BitsPerSample / 8)));

    /// <summary>Read raw PCM bytes. Returns 0 at end of the data chunk.</summary>
    public int Read(Span<byte> buffer)
    {
        if (this.remaining <= 0 || buffer.IsEmpty)
            return 0;

        if (buffer.Length > this.remaining)
            buffer = buffer[..(int)this.remaining];

        var read = this.input.Read(buffer);
        this.remaining -= read;
        return read;
    }

    /// <summary>
    /// Read 16-bit samples. Only valid for 16-bit files. Returns the number of samples read, 0 at
    /// end of data.
    /// </summary>
    public int ReadSamples(Span<short> samples)
    {
        if (this.BitsPerSample != 16)
            throw new InvalidOperationException($"This file is {this.BitsPerSample}-bit; the short overload only applies to 16-bit PCM.");

        var read = this.Read(MemoryMarshal.AsBytes(samples));
        var count = read / 2;

        if (!BitConverter.IsLittleEndian)
        {
            var bytes = MemoryMarshal.AsBytes(samples[..count]);
            for (var i = 0; i < count; i++)
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(i * 2)..]);
        }
        return count;
    }

    /// <summary>Open a WAV file from disk.</summary>
    public static WavReader Open(string path)
        => new(File.OpenRead(path));

    static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        if (!TryReadExactly(stream, buffer))
            throw new EndOfStreamException("WAV stream ended unexpectedly.");
    }

    static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
                return false;
            total += read;
        }
        return true;
    }

    static void Skip(Stream stream, int count)
    {
        if (count <= 0)
            return;

        if (stream.CanSeek)
        {
            stream.Position += count;
            return;
        }

        Span<byte> sink = stackalloc byte[256];
        while (count > 0)
        {
            var take = Math.Min(sink.Length, count);
            if (!TryReadExactly(stream, sink[..take]))
                return;
            count -= take;
        }
    }

    public void Dispose()
    {
        if (!this.leaveOpen)
            this.input.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!this.leaveOpen)
            await this.input.DisposeAsync();
    }
}
