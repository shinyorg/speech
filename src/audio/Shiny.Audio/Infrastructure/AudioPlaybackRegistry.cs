namespace Shiny.Audio.Infrastructure;

/// <summary>
/// Tracks the clips a platform <see cref="IAudioPlayer"/> currently has in flight, so several can
/// play at once. The player asks for a handle per clip and the handle removes itself when the clip
/// ends, is stopped, or is cancelled.
/// </summary>
/// <remarks>
/// This is the piece every platform player shares — the native plumbing (AVAudioPlayer, MediaPlayer,
/// an &lt;audio&gt; element, a PCM pump) is attached per handle through
/// <see cref="AudioPlayback.OnStop"/>.
/// </remarks>
public sealed class AudioPlaybackRegistry
{
    readonly List<AudioPlayback> active = new();
    readonly Lock sync = new();

    /// <summary>Every playback that has not finished yet, oldest first.</summary>
    public IReadOnlyList<IAudioPlayback> Active
    {
        get
        {
            lock (this.sync)
                return this.active.ToArray();
        }
    }

    /// <summary>True while at least one playback is running.</summary>
    public bool IsPlaying
    {
        get
        {
            lock (this.sync)
                return this.active.Count > 0;
        }
    }

    /// <summary>
    /// Creates and registers a handle for a clip that is about to start. The caller then attaches
    /// its native teardown with <see cref="AudioPlayback.OnStop"/> and hooks the caller's
    /// cancellation token with <see cref="AudioPlayback.CancelWith"/>.
    /// </summary>
    /// <param name="source">The URL / file path the clip came from, or <c>null</c> for a stream.</param>
    public AudioPlayback Create(string? source)
    {
        var playback = new AudioPlayback(this, source);
        lock (this.sync)
            this.active.Add(playback);

        return playback;
    }

    /// <summary>
    /// Stops every playback and waits for each one's native teardown.
    /// </summary>
    public Task StopAllAsync()
    {
        AudioPlayback[] snapshot;
        lock (this.sync)
            snapshot = this.active.ToArray();

        return snapshot.Length == 0
            ? Task.CompletedTask
            : Task.WhenAll(snapshot.Select(x => x.StopAsync()));
    }

    /// <summary>
    /// Records the current output level of one playback and returns the loudest across all of them.
    /// </summary>
    /// <remarks>
    /// Concurrent clips share a single <see cref="IAudioPlayer.AudioLevelChanged"/> stream, so what a
    /// VU meter should show is the loudest thing currently audible — not whichever clip happened to
    /// report last. Platforms that meter every clip on one timer (Apple) can take the max themselves
    /// and skip this.
    /// </remarks>
    public double ReportLevel(AudioPlayback playback, double level)
    {
        lock (this.sync)
        {
            playback.Level = level;

            var max = 0d;
            foreach (var item in this.active)
                max = Math.Max(max, item.Level);

            return max;
        }
    }

    internal void Remove(AudioPlayback playback)
    {
        lock (this.sync)
            this.active.Remove(playback);
    }
}
