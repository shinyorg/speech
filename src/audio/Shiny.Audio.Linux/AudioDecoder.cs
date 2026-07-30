using System.Buffers.Binary;
using NLayer;

namespace Shiny.Audio;

/// <summary>Decoded interleaved signed 16-bit little-endian PCM, plus the format it plays at.</summary>
readonly record struct DecodedAudio(byte[] Pcm, int SampleRate, int Channels);

/// <summary>
/// Decodes the audio a text-to-speech provider returns into raw PCM for the Linux backends.
/// </summary>
/// <remarks>
/// Linux has no system-wide media decoder to call the way <c>MediaPlayer</c> (Windows/Android) or
/// <c>AVAudioPlayer</c> (Apple) does, so the formats the cloud providers actually emit are decoded
/// in managed code: MP3 (Azure, OpenAI and ElevenLabs all default to it) via NLayer, and WAV/PCM
/// directly. The container is detected from the bytes rather than trusted from a content type.
/// </remarks>
static class AudioDecoder
{
    /// <summary>
    /// Read <paramref name="stream"/> to the end and decode it.
    /// </summary>
    /// <exception cref="NotSupportedException">The container isn't MP3 or WAV.</exception>
    internal static async Task<DecodedAudio> DecodeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffered = new MemoryStream();
        await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
        buffered.Position = 0;

        var bytes = buffered.GetBuffer().AsSpan(0, (int)buffered.Length);
        if (bytes.Length < 4)
            throw new NotSupportedException("Audio stream is empty or too short to identify.");

        if (IsWav(bytes))
            return DecodeWav(bytes);

        if (IsMp3(bytes))
        {
            buffered.Position = 0;
            return DecodeMp3(buffered);
        }

        throw new NotSupportedException(
            $"Unsupported audio format (leading bytes {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}). " +
            "The Linux player decodes MP3 and WAV; request one of those output formats from your provider."
        );
    }

    static bool IsWav(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 &&
        bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
        bytes[8] == 'W' && bytes[9] == 'A' && bytes[10] == 'V' && bytes[11] == 'E';

    static bool IsMp3(ReadOnlySpan<byte> bytes) =>
        // An ID3v2 tag, or a raw frame sync (11 set bits).
        (bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3') ||
        (bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0);

    static DecodedAudio DecodeMp3(Stream stream)
    {
        using var mpeg = new MpegFile(stream);

        // ReadSamplesInt16 emits exactly the S16LE the backends want, so there's no float round-trip.
        var chunk = new byte[16384];
        using var pcm = new MemoryStream();

        int read;
        while ((read = mpeg.ReadSamplesInt16(chunk, 0, chunk.Length)) > 0)
            pcm.Write(chunk, 0, read);

        return new DecodedAudio(pcm.ToArray(), mpeg.SampleRate, mpeg.Channels);
    }

    static DecodedAudio DecodeWav(ReadOnlySpan<byte> bytes)
    {
        // Walk the RIFF chunk list rather than assuming the canonical 44-byte header — real files
        // carry LIST/fact chunks ahead of the data.
        var offset = 12;
        int channels = 0, sampleRate = 0, bitsPerSample = 0, formatTag = 0;

        while (offset + 8 <= bytes.Length)
        {
            var chunkId = bytes.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var body = offset + 8;

            if (chunkSize < 0 || body + chunkSize > bytes.Length)
                chunkSize = bytes.Length - body;   // tolerate a truncated or streaming-written size

            if (chunkId[0] == 'f' && chunkId[1] == 'm' && chunkId[2] == 't' && chunkId[3] == ' ' && chunkSize >= 16)
            {
                formatTag = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(body, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(body + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(body + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(body + 14, 2));
            }
            else if (chunkId[0] == 'd' && chunkId[1] == 'a' && chunkId[2] == 't' && chunkId[3] == 'a')
            {
                if (channels == 0 || sampleRate == 0)
                    throw new NotSupportedException("WAV data chunk appeared before its format chunk.");

                return new DecodedAudio(
                    ToPcm16(bytes.Slice(body, chunkSize), formatTag, bitsPerSample),
                    sampleRate,
                    channels
                );
            }

            offset = body + chunkSize + (chunkSize & 1);   // chunks are word-aligned
        }

        throw new NotSupportedException("WAV stream contains no data chunk.");
    }

    static byte[] ToPcm16(ReadOnlySpan<byte> data, int formatTag, int bitsPerSample)
    {
        const int FormatPcm = 1;
        const int FormatFloat = 3;
        const int FormatExtensible = 0xFFFE;

        // WAVE_FORMAT_EXTENSIBLE carries the real tag in a sub-format GUID; for the integer widths
        // below the samples are laid out identically, so bit depth is enough to decode it.
        if (formatTag is FormatPcm or FormatExtensible)
        {
            switch (bitsPerSample)
            {
                case 16:
                    return data.ToArray();

                case 8:
                {
                    // 8-bit WAV is unsigned, centred on 128.
                    var pcm = new byte[data.Length * 2];
                    for (var i = 0; i < data.Length; i++)
                        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), (short)((data[i] - 128) << 8));

                    return pcm;
                }

                case 24:
                {
                    var samples = data.Length / 3;
                    var pcm = new byte[samples * 2];
                    for (var i = 0; i < samples; i++)
                    {
                        // Keep the top 16 bits of each little-endian 24-bit sample.
                        var value = (short)(data[i * 3 + 1] | (data[i * 3 + 2] << 8));
                        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), value);
                    }
                    return pcm;
                }

                case 32:
                {
                    var samples = data.Length / 4;
                    var pcm = new byte[samples * 2];
                    for (var i = 0; i < samples; i++)
                    {
                        var value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * 4, 4));
                        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), (short)(value >> 16));
                    }
                    return pcm;
                }
            }
        }
        else if (formatTag == FormatFloat && bitsPerSample == 32)
        {
            var samples = data.Length / 4;
            var pcm = new byte[samples * 2];
            for (var i = 0; i < samples; i++)
            {
                var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * 4, 4)));
                var scaled = (short)Math.Clamp(value * 32767f, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), scaled);
            }
            return pcm;
        }

        throw new NotSupportedException($"Unsupported WAV encoding (format tag {formatTag}, {bitsPerSample}-bit).");
    }
}
