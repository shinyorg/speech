namespace Shiny.Audio;

/// <summary>
/// Maps raw audio amplitude to a perceptual 0–1 meter value (a VU meter). Voice RMS is tiny in
/// linear terms (~0.03–0.1), so a raw value barely moves a bar; converting to dBFS and mapping a
/// useful range makes the meter visibly track speech.
/// </summary>
/// <remarks>
/// Every level event in this library (<see cref="IAudioSource.InputLevelChanged"/>,
/// <see cref="IAudioMonitor.InputLevelChanged"/>, <see cref="IAudioPlayer.AudioLevelChanged"/>) is
/// normalized through here, so input and output meters read on the same scale. Use it directly when
/// you consume the raw PCM stream from <see cref="IAudioSource.StartCaptureAsync"/> yourself.
/// </remarks>
public static class AudioLevel
{
    /// <summary>Below this dBFS level the signal reads as silence (0.0).</summary>
    public const double NoiseFloorDb = -50.0;

    /// <summary>Map a linear RMS amplitude (0–1) to a perceptual meter value (0–1).</summary>
    public static double FromRms(double rms)
    {
        if (rms <= 0)
            return 0;

        var db = 20.0 * Math.Log10(rms);   // dBFS (<= 0)
        if (db <= NoiseFloorDb)
            return 0;
        if (db >= 0)
            return 1;

        return (db - NoiseFloorDb) / -NoiseFloorDb;   // map [NoiseFloorDb, 0] -> [0, 1]
    }

    /// <summary>
    /// Compute a meter value (0–1) from a buffer of 16-bit little-endian PCM samples — the format
    /// <see cref="IAudioSource"/> produces. Channel count doesn't matter; every sample is included.
    /// </summary>
    public static double FromPcm16(ReadOnlySpan<byte> pcm)
    {
        var samples = pcm.Length / 2;
        if (samples == 0)
            return 0;

        double sum = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var s = (short)(pcm[i] | (pcm[i + 1] << 8)) / 32768.0;
            sum += s * s;
        }
        return FromRms(Math.Sqrt(sum / samples));
    }

    /// <summary>
    /// Compute a meter value (0–1) from normalized (-1–1) float samples — the format the platform
    /// capture graphs hand back before conversion to the PCM16 contract.
    /// </summary>
    public static double FromSamples(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
            return 0;

        double sum = 0;
        foreach (var s in samples)
            sum += (double)s * s;

        return FromRms(Math.Sqrt(sum / samples.Length));
    }
}
