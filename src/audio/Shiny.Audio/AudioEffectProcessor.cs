namespace Shiny.Audio;

/// <summary>
/// Applies an <see cref="AudioEffectChain"/> to audio that already exists, rather than to a live
/// microphone.
/// </summary>
/// <remarks>
/// This is the companion to recording dry: keep the clean take, then render it with whatever
/// settings you like, as many times as you like, without asking anyone to perform it again.
/// </remarks>
public static class AudioEffectProcessor
{
    const int BlockSamples = 1024;

    /// <summary>Read a WAV file, run it through the chain, and write the result to another WAV file.</summary>
    /// <param name="inputPath">Source file. Must be uncompressed 16-bit PCM.</param>
    /// <param name="outputPath">Destination. Overwritten if it exists; parent directories are created.</param>
    /// <param name="effects">The chain to apply. Its state is reset before processing begins.</param>
    /// <returns>The duration of the audio written.</returns>
    public static TimeSpan ProcessFile(string inputPath, string outputPath, AudioEffectChain effects)
    {
        ArgumentNullException.ThrowIfNull(effects);

        using var reader = WavReader.Open(inputPath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!String.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var writer = new WavWriter(
            File.Create(outputPath),
            reader.SampleRate,
            reader.Channels,
            reader.BitsPerSample
        );

        Process(reader, writer, effects);
        return writer.Duration;
    }

    /// <summary>
    /// Run a chain over a buffer of PCM16 samples in place — the simplest form, for audio already
    /// in memory.
    /// </summary>
    public static void Process(Span<short> samples, AudioEffectChain effects, int sampleRate = 16000)
    {
        ArgumentNullException.ThrowIfNull(effects);
        effects.Reset();

        // Fed in blocks rather than one giant call so the result matches what live capture produces:
        // parameter smoothing and the bypass crossfade both advance per buffer.
        for (var offset = 0; offset < samples.Length; offset += BlockSamples)
        {
            var count = Math.Min(BlockSamples, samples.Length - offset);
            effects.Process(samples.Slice(offset, count), sampleRate);
        }
    }

    static void Process(WavReader reader, WavWriter writer, AudioEffectChain effects)
    {
        if (reader.BitsPerSample != 16)
            throw new NotSupportedException($"Only 16-bit PCM can be processed (file is {reader.BitsPerSample}-bit).");

        effects.Reset();

        var buffer = new short[BlockSamples];
        int read;
        while ((read = reader.ReadSamples(buffer)) > 0)
        {
            effects.Process(buffer.AsSpan(0, read), reader.SampleRate);
            writer.Write(buffer.AsSpan(0, read));
        }
    }
}
