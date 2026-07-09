namespace Shiny.Audio;

/// <summary>
/// Maps a raw linear RMS amplitude (0–1) to a perceptual 0–1 meter value. Voice RMS is tiny in
/// linear terms (~0.03–0.1), so a raw value barely moves a bar; converting to dBFS and mapping a
/// useful range makes the meter visibly track speech — the same idea as the playback VU meters.
/// </summary>
static class AudioLevel
{
    const double NoiseFloorDb = -50.0;   // below this reads as silence

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
}
