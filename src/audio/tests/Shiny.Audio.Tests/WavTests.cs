using System.Buffers.Binary;
using Shiny.Audio;

namespace Shiny.Audio.Tests;

public class WavTests
{
    static byte[] Pcm(int bytes)
    {
        var data = new byte[bytes];
        for (var i = 0; i < bytes; i++)
            data[i] = (byte)(i % 251);
        return data;
    }

    [Test]
    public async Task Header_IsCanonical()
    {
        var file = WavWriter.CreateFile(Pcm(100), 16000, 1, 16);

        await Assert.That(file.Length).IsEqualTo(WavWriter.HeaderSize + 100);
        await Assert.That(file.AsSpan(0, 4).SequenceEqual("RIFF"u8)).IsTrue();
        await Assert.That(file.AsSpan(8, 4).SequenceEqual("WAVE"u8)).IsTrue();
        await Assert.That(file.AsSpan(12, 4).SequenceEqual("fmt "u8)).IsTrue();
        await Assert.That(file.AsSpan(36, 4).SequenceEqual("data"u8)).IsTrue();

        await Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4, 4))).IsEqualTo(36 + 100);
        await Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(16, 4))).IsEqualTo(16);
        await Assert.That(BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(20, 2))).IsEqualTo((short)1);
        await Assert.That(BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(22, 2))).IsEqualTo((short)1);
        await Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(24, 4))).IsEqualTo(16000);
        await Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(28, 4))).IsEqualTo(32000);
        await Assert.That(BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(32, 2))).IsEqualTo((short)2);
        await Assert.That(BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(34, 2))).IsEqualTo((short)16);
        await Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(40, 4))).IsEqualTo(100);
    }

    [Test]
    public async Task Header_StereoAndRateAreReflected()
    {
        var file = WavWriter.CreateFile(Pcm(80), 44100, 2, 16);

        await Assert.That(BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(22, 2))).IsEqualTo((short)2);
        await Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(24, 4))).IsEqualTo(44100);
        await Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(28, 4))).IsEqualTo(44100 * 4);
        await Assert.That(BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(32, 2))).IsEqualTo((short)4);
    }

    [Test]
    public async Task StreamingWriter_PatchesSizesOnFinish()
    {
        var pcm = Pcm(1024);
        var ms = new MemoryStream();

        using (var writer = new WavWriter(ms, leaveOpen: true))
        {
            // Written in pieces to prove the size patching does not depend on a single write.
            writer.Write(pcm.AsSpan(0, 300));
            writer.Write(pcm.AsSpan(300, 724));
            await Assert.That(writer.DataLength).IsEqualTo(1024L);
        }

        var file = ms.ToArray();
        await Assert.That(file.Length).IsEqualTo(WavWriter.HeaderSize + 1024);
        await Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4, 4))).IsEqualTo(36 + 1024);
        await Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(40, 4))).IsEqualTo(1024);
        await Assert.That(file.AsSpan(WavWriter.HeaderSize).SequenceEqual(pcm)).IsTrue();
    }

    [Test]
    public async Task StreamingWriter_MatchesCreateFile()
    {
        var pcm = Pcm(512);
        var ms = new MemoryStream();
        using (var writer = new WavWriter(ms, leaveOpen: true))
            writer.Write(pcm);

        await Assert.That(ms.ToArray().SequenceEqual(WavWriter.CreateFile(pcm))).IsTrue();
    }

    [Test]
    public async Task Duration_TracksBytesWritten()
    {
        var ms = new MemoryStream();
        using var writer = new WavWriter(ms, 16000, 1, 16, leaveOpen: true);

        writer.Write(new short[16000]);   // exactly one second of 16 kHz mono
        await Assert.That(writer.Duration.TotalSeconds).IsEqualTo(1.0).Within(0.0001);
    }

    [Test]
    public async Task Finish_IsIdempotent()
    {
        var ms = new MemoryStream();
        var writer = new WavWriter(ms, leaveOpen: true);
        writer.Write(Pcm(64));

        writer.Finish();
        writer.Finish();
        writer.Dispose();

        await Assert.That(ms.ToArray().Length).IsEqualTo(WavWriter.HeaderSize + 64);
    }

    [Test]
    public async Task RoundTrip_PreservesFormatAndData()
    {
        var pcm = Pcm(4096);
        var file = WavWriter.CreateFile(pcm, 22050, 1, 16);

        using var reader = new WavReader(new MemoryStream(file));
        await Assert.That(reader.SampleRate).IsEqualTo(22050);
        await Assert.That(reader.Channels).IsEqualTo(1);
        await Assert.That(reader.BitsPerSample).IsEqualTo(16);
        await Assert.That(reader.DataLength).IsEqualTo(4096L);

        var read = new byte[8192];
        var total = 0;
        int n;
        while ((n = reader.Read(read.AsSpan(total))) > 0)
            total += n;

        await Assert.That(total).IsEqualTo(4096);
        await Assert.That(read.AsSpan(0, 4096).SequenceEqual(pcm)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_PreservesSamples()
    {
        var samples = new short[2000];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(i * 13 - 16000);

        var ms = new MemoryStream();
        using (var writer = new WavWriter(ms, leaveOpen: true))
            writer.Write(samples);

        ms.Position = 0;
        using var reader = new WavReader(ms);
        var read = new short[samples.Length];
        var total = 0;
        int n;
        while ((n = reader.ReadSamples(read.AsSpan(total))) > 0)
            total += n;

        await Assert.That(total).IsEqualTo(samples.Length);
        await Assert.That(read.SequenceEqual(samples)).IsTrue();
    }

    [Test]
    public async Task Reader_SkipsUnknownChunks()
    {
        // A LIST chunk between fmt and data is common in real files and must not break parsing.
        var pcm = Pcm(64);
        var ms = new MemoryStream();

        Span<byte> header = stackalloc byte[WavWriter.HeaderSize];
        WavWriter.WriteHeader(header, 16000, 1, 16, pcm.Length);

        ms.Write(header[..36]);                       // RIFF + fmt
        ms.Write("LIST"u8);
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, 6);
        ms.Write(size);
        ms.Write(new byte[6]);
        ms.Write(header[36..44]);                     // data chunk header
        ms.Write(pcm);
        ms.Position = 0;

        using var reader = new WavReader(ms);
        await Assert.That(reader.SampleRate).IsEqualTo(16000);
        await Assert.That(reader.DataLength).IsEqualTo(64L);

        var read = new byte[64];
        await Assert.That(reader.Read(read)).IsEqualTo(64);
        await Assert.That(read.SequenceEqual(pcm)).IsTrue();
    }

    [Test]
    public async Task Reader_RejectsNonWav()
    {
        await Assert.That(() => new WavReader(new MemoryStream(new byte[64])))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task Reader_RejectsCompressedFormat()
    {
        Span<byte> header = stackalloc byte[WavWriter.HeaderSize];
        WavWriter.WriteHeader(header, 16000, 1, 16, 0);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..22], 0x0011);   // IMA ADPCM

        var bytes = header.ToArray();
        await Assert.That(() => new WavReader(new MemoryStream(bytes)))
            .Throws<NotSupportedException>();
    }
}
