using Microsoft.Extensions.Logging;
using Shiny.Audio.Interop;

namespace Shiny.Audio;

/// <summary>
/// Audio playback on Linux over PulseAudio/PipeWire, falling back to ALSA.
/// </summary>
/// <remarks>
/// Unlike the other platforms there is no system media player to hand a stream to, so MP3/WAV is
/// decoded in managed code (see <see cref="AudioDecoder"/>) and pushed to the backend as PCM. That
/// has one upside: the player sees every sample, so output metering is supported here where it
/// isn't on Windows.
/// </remarks>
public class LinuxAudioPlayer : IAudioPlayer, IDisposable
{
    readonly ILogger<LinuxAudioPlayer> logger;
    readonly Lock sync = new();

    PcmStream? stream;
    CancellationTokenSource? cts;
    Task? playbackTask;

    IDisposable? volumeSubscription;
    float lastKnownVolume = 1f;

    public LinuxAudioPlayer(ILogger<LinuxAudioPlayer> logger)
    {
        this.logger = logger;
    }

    public bool IsPlaying { get; private set; }

    /// <summary>Always true — playback runs through managed PCM, so every sample can be metered.</summary>
    public bool IsPlayerAnalysisSupported => true;

    public event EventHandler<double>? AudioLevelChanged;

    public async Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        var decoded = await AudioDecoder.DecodeAsync(audioStream, cancellationToken).ConfigureAwait(false);
        if (decoded.Pcm.Length == 0)
            return;

        await this.StopAsync().ConfigureAwait(false);

        // Opened at the decoded file's own rate — the backend resamples to the hardware rate, so
        // there is no resampler in this library.
        var pcm = PcmStream.OpenPlayback(decoded.SampleRate, decoded.Channels, null);
        var source = new CancellationTokenSource();

        lock (this.sync)
        {
            this.stream = pcm;
            this.cts = source;
            this.IsPlaying = true;
        }

        this.logger.LogDebug(
            "Linux audio playback started on {Backend} ({Rate}Hz, {Channels}ch, {Bytes} bytes)",
            PcmStream.Backend, decoded.SampleRate, decoded.Channels, decoded.Pcm.Length
        );

        using var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, source.Token);
        var token = link.Token;

        // Writes block in native code for the length of the clip, so keep them off the caller's thread.
        var task = Task.Factory.StartNew(
            () => this.Pump(pcm, decoded, token),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );

        lock (this.sync)
            this.playbackTask = task;

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stop() or the caller's token — not an error.
        }
        finally
        {
            lock (this.sync)
                this.IsPlaying = false;

            this.logger.LogDebug("Linux audio playback finished");
        }
    }

    void Pump(PcmStream pcm, DecodedAudio decoded, CancellationToken token)
    {
        // 20ms per write keeps cancellation responsive without thrashing the backend.
        var bytesPerFrame = decoded.Channels * 2;
        var chunk = Math.Max(bytesPerFrame, decoded.SampleRate / 50 * bytesPerFrame);
        var throttle = new AudioLevelThrottle();

        var offset = 0;
        while (offset < decoded.Pcm.Length)
        {
            token.ThrowIfCancellationRequested();

            var count = Math.Min(chunk, decoded.Pcm.Length - offset);

            if (throttle.TryEmit(AudioLevel.FromPcm16(decoded.Pcm.AsSpan(offset, count)), out var level))
                this.AudioLevelChanged?.Invoke(this, level);

            // A trailing partial frame would desynchronise the interleaving; drop it.
            count -= count % bytesPerFrame;
            if (count == 0)
                break;

            pcm.Write(decoded.Pcm, offset, count);
            offset += count;
        }

        if (!token.IsCancellationRequested)
            pcm.Drain();
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? source;
        Task? task;
        PcmStream? pcm;

        lock (this.sync)
        {
            source = this.cts;
            task = this.playbackTask;
            pcm = this.stream;
            this.cts = null;
            this.playbackTask = null;
            this.stream = null;
        }

        if (source == null)
            return;

        await source.CancelAsync().ConfigureAwait(false);

        if (task != null)
        {
            try { await task.ConfigureAwait(false); }
            catch { /* cancellation, or an error already logged by the pump */ }
        }

        pcm?.Dispose();
        source.Dispose();

        lock (this.sync)
            this.IsPlaying = false;

        this.logger.LogDebug("Linux audio playback stopped");
    }

    #region volume

    /// <summary>
    /// Settable through PulseAudio/PipeWire, which owns the default sink's volume. The ALSA
    /// fallback has no equivalent without binding the whole <c>snd_mixer</c> API, so it reports
    /// unsupported rather than silently doing nothing.
    /// </summary>
    public bool IsVolumeControlSupported => PcmStream.Backend == LinuxAudioBackend.PulseAudio;

    public float Volume
    {
        get
        {
            if (PcmStream.Backend != LinuxAudioBackend.PulseAudio)
                return this.lastKnownVolume;

            // A server round-trip per read. Volume is read on demand (not polled), so this stays
            // cheap in practice, and it is the only way to see changes made outside this process.
            this.lastKnownVolume = PulseIntrospect.GetSinkVolume(null) ?? this.lastKnownVolume;
            return this.lastKnownVolume;
        }
        set
        {
            if (!this.IsVolumeControlSupported)
                throw new NotSupportedException(
                    "Setting the system volume requires PulseAudio or PipeWire; the ALSA fallback cannot set it. " +
                    "Check IsVolumeControlSupported first."
                );

            var clamped = Math.Clamp(value, 0f, 1f);
            if (!PulseIntrospect.SetSinkVolume(null, clamped))
                throw new InvalidOperationException("Failed to set the PulseAudio sink volume.");

            this.lastKnownVolume = clamped;
            this.volumeChanged?.Invoke(this, clamped);
        }
    }

    event EventHandler<float>? volumeChanged;

    public event EventHandler<float>? VolumeChanged
    {
        add
        {
            this.volumeChanged += value;
            this.EnsureVolumeSubscription();
        }
        remove => this.volumeChanged -= value;
    }

    /// <summary>
    /// PulseAudio's subscription fires for any sink/source/server change, so the volume is re-read
    /// and the event raised only when it actually moved.
    /// </summary>
    void EnsureVolumeSubscription()
    {
        lock (this.sync)
        {
            if (this.volumeSubscription != null || PcmStream.Backend != LinuxAudioBackend.PulseAudio)
                return;

            this.volumeSubscription = PulseIntrospect.Subscribe(() =>
            {
                var current = PulseIntrospect.GetSinkVolume(null);
                if (current == null || Math.Abs(current.Value - this.lastKnownVolume) < 0.001f)
                    return;

                this.lastKnownVolume = current.Value;
                this.volumeChanged?.Invoke(this, current.Value);
            });
        }
    }

    #endregion

    public void Dispose()
    {
        this.volumeSubscription?.Dispose();
        this.volumeSubscription = null;
        this.stream?.Dispose();
        this.stream = null;
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
        this.Dispose();
    }
}
