namespace Shiny.Audio;

/// <summary>
/// Rate-limits meter events to a UI-friendly cadence. Capture callbacks fire far faster than a bar
/// needs to redraw (Windows' AudioGraph runs a 10ms quantum, the browser worklet is finer still),
/// and marshalling every one of those to the UI thread is pure overhead. Levels are peak-held
/// between emissions so a short transient still shows up rather than being sampled away.
/// </summary>
/// <remarks>Call from a single capture thread — one instance per capture session, no locking.</remarks>
sealed class AudioLevelThrottle(int intervalMs = 50)
{
    long nextEmit;
    double peak;

    /// <summary>
    /// Feed a computed level. Returns true (with the peak since the last emission) when enough time
    /// has passed to raise the event, false when the sample should just be folded into the peak.
    /// </summary>
    public bool TryEmit(double level, out double value)
    {
        if (level > this.peak)
            this.peak = level;

        var now = Environment.TickCount64;
        if (now < this.nextEmit)
        {
            value = 0;
            return false;
        }

        this.nextEmit = now + intervalMs;
        value = this.peak;
        this.peak = 0;
        return true;
    }
}
