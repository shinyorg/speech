using AVFoundation;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using Shiny.Audio.Infrastructure;

namespace Shiny.Audio;

public class AppleAudioPlayer(ILogger<AppleAudioPlayer> logger) : IAudioPlayer
{
    readonly AudioPlaybackRegistry playbacks = new();

    // One AVAudioPlayer per clip, kept alongside its handle so the single metering timer can sample
    // all of them and report the loudest.
    readonly List<(AudioPlayback Playback, AVAudioPlayer Native)> metered = new();
    readonly Lock meterSync = new();
    NSTimer? meterTimer;

    public bool IsPlaying => this.playbacks.IsPlaying;
    public IReadOnlyList<IAudioPlayback> Active => this.playbacks.Active;
    public bool IsPlayerAnalysisSupported => true;
    public event EventHandler<double>? AudioLevelChanged;

#if MACOS
    // macOS: CoreAudio HAL — reading AND setting the system output volume are supported.
    MacSystemVolume? macVolume;
    MacSystemVolume MacVolume => this.macVolume ??= CreateMacVolume();

    MacSystemVolume CreateMacVolume()
    {
        var mv = new MacSystemVolume();
        mv.Changed += v => this.volumeChanged?.Invoke(this, v);
        return mv;
    }

    public bool IsVolumeControlSupported => this.MacVolume.CanSet;
    public float Volume
    {
        get => this.MacVolume.Get();
        set => this.MacVolume.Set(value);   // the CoreAudio listener raises VolumeChanged
    }

    event EventHandler<float>? volumeChanged;
    public event EventHandler<float>? VolumeChanged
    {
        add
        {
            this.volumeChanged += value;
            _ = this.MacVolume;   // ensure the CoreAudio listener is registered
        }
        remove => this.volumeChanged -= value;
    }
#else
    // iOS / Mac Catalyst: AVAudioSession.OutputVolume is read-only. There is no supported API to set the
    // system volume (MPMusicPlayerController.Volume was deprecated in iOS 7 and is a no-op), so reading and
    // KVO observation work, but the setter throws.
    IDisposable? volumeObserver;

    public bool IsVolumeControlSupported => false;
    public float Volume
    {
        get
        {
            EnsureSessionActive();
            return AVAudioSession.SharedInstance().OutputVolume;
        }
        set => throw new NotSupportedException(
            "Setting the system volume is not supported on iOS / Mac Catalyst. Check IAudioPlayer.IsVolumeControlSupported before setting, and let the user adjust volume with the hardware buttons or an MPVolumeView.");
    }

    event EventHandler<float>? volumeChanged;
    public event EventHandler<float>? VolumeChanged
    {
        add
        {
            this.volumeChanged += value;
            this.EnsureVolumeObserver();
        }
        remove => this.volumeChanged -= value;
    }

    // KVO on AVAudioSession.outputVolume — fires for hardware buttons and Control Center. OutputVolume only
    // reflects reality while the session is active, so make sure it is. Registered lazily and disposed in
    // StopObserving/DisposeAsync.
    void EnsureVolumeObserver()
    {
        if (this.volumeObserver != null)
            return;

        EnsureSessionActive();
        var session = AVAudioSession.SharedInstance();
        this.volumeObserver = session.AddObserver("outputVolume", NSKeyValueObservingOptions.New, change =>
        {
            var volume = (change.NewValue as NSNumber)?.FloatValue ?? session.OutputVolume;
            this.volumeChanged?.Invoke(this, volume);
        });
    }

    static void EnsureSessionActive()
    {
        // Activate without disturbing whatever category is set (mix with others so we never grab audio focus
        // just to read a volume). Cheap and idempotent.
        var session = AVAudioSession.SharedInstance();
        session.SetActive(true, out _);
    }
#endif

    public async Task<IAudioPlayback> StartAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, cancellationToken);

        var native = AVAudioPlayer.FromData(NSData.FromArray(ms.ToArray()));
        if (native == null)
            throw new InvalidOperationException("Failed to create audio player from data");

        return StartCore(native, null, cancellationToken);
    }

    public async Task<IAudioPlayback> StartAsync(string source, CancellationToken cancellationToken = default)
    {
        AVAudioPlayer? native;
        if (PlaybackSource.IsRemote(source))
        {
            // AVAudioPlayer cannot stream a remote URL, so buffer it into memory first
            // (keeps metering working, same as the stream path).
            var bytes = await PlaybackSource.DownloadAsync(source, cancellationToken);
            native = AVAudioPlayer.FromData(NSData.FromArray(bytes));
        }
        else
        {
            native = AVAudioPlayer.FromUrl(NSUrl.FromFilename(source), out _);
        }

        if (native == null)
            throw new InvalidOperationException($"Failed to create audio player from source: {source}");

        return StartCore(native, source, cancellationToken);
    }

    IAudioPlayback StartCore(AVAudioPlayer native, string? source, CancellationToken cancellationToken)
    {
        native.MeteringEnabled = true;

#if !MACOS
        // If something else (e.g. an active STT session) has already configured PlayAndRecord,
        // leave it alone. Switching to Playback-only would suspend the microphone and break any
        // concurrent recognition. Always reactivate the session in case it was deactivated.
        // Preserve the current category options so we don't tear down another component's ducking
        // (e.g. Shiny.Music's Duck() sets Playback + DuckOthers and expects this playback to be heard
        // over the ducked music). We do NOT force DefaultToSpeaker: it's a no-op for plain Playback
        // (which already defaults to the main speaker) and pins output local, preventing playback from
        // following the system output route to headphones / Bluetooth / AirPlay (HomePod).
        //
        // Always OR in MixWithOthers so we never hold an EXCLUSIVE Playback session. This player
        // activates the shared session and never deactivates it, so a bare announcement (no duck
        // active, CategoryOptions == 0) would otherwise leave the session active with exclusive
        // Playback. That silently ducks any OUT-OF-PROCESS audio — notably a walkout song played via
        // MPMusicPlayerController (Shiny.Music) — so the next song "starts really quiet" until
        // something flips the session back to mixing. MixWithOthers keeps us non-exclusive; it does
        // NOT cancel a concurrent DuckOthers (they combine), so a real walkout announcement is still
        // heard over its ducked music.
        var session = AVAudioSession.SharedInstance();
        var playAndRecord = AVAudioSessionCategory.PlayAndRecord.GetConstant();
        if (session.Category != playAndRecord)
            session.SetCategory(
                AVAudioSessionCategory.Playback,
                session.CategoryOptions | AVAudioSessionCategoryOptions.MixWithOthers,
                out _);
        session.SetActive(true, out _);
#endif

        var playback = this.playbacks.Create(source);

        // FinishedPlaying runs on AVAudioPlayer's native callback stack, and disposing the player
        // from there is what the runtime reports as "player object was Dispose()d during the callback
        // ... corrupted the state of the program". AudioPlayback.Complete() only marks the handle
        // finished and hops to the thread pool before running the teardown below, so disposal always
        // happens on a clean stack.
        void OnFinishedPlaying(object? sender, AVStatusEventArgs e) => playback.Complete();
        native.FinishedPlaying += OnFinishedPlaying;

        lock (this.meterSync)
            this.metered.Add((playback, native));

        playback.OnStop(() =>
        {
            // Under meterSync so the metering timer can never sample a player mid-disposal.
            lock (this.meterSync)
            {
                this.metered.RemoveAll(x => x.Playback.Id == playback.Id);
                native.FinishedPlaying -= OnFinishedPlaying;
                native.Stop();
                native.Dispose();
            }

            StopMeterTimerIfIdle();
            logger.LogDebug("Apple audio playback stopped ({Source})", source);
            return Task.CompletedTask;
        });

        native.Play();
        StartMeterTimer();

        // Linked last so an already-cancelled token tears down a fully constructed playback.
        playback.CancelWith(cancellationToken);
        logger.LogDebug("Apple audio playback started ({Source})", source);
        return playback;
    }

    // One timer for the player, not one per clip: it samples every live AVAudioPlayer and reports the
    // loudest, so a VU meter shows what is actually audible when clips overlap.
    void StartMeterTimer()
        => DispatchQueue.MainQueue.DispatchAsync(() =>
            meterTimer ??= NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromMilliseconds(50), _ => SampleMeters())
        );

    void StopMeterTimerIfIdle()
        => DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            lock (this.meterSync)
            {
                if (this.metered.Count > 0)
                    return;
            }

            meterTimer?.Invalidate();
            meterTimer = null;
        });

    void SampleMeters()
    {
        var level = 0.0;
        var any = false;

        // Held for the whole sweep so a concurrent teardown cannot dispose a player out from under it.
        lock (this.meterSync)
        {
            foreach (var (_, native) in this.metered)
            {
                if (!native.Playing)
                    continue;

                native.UpdateMeters();
                level = Math.Max(level, DbToLinear(native.AveragePower(0)));
                any = true;
            }
        }

        if (any)
            AudioLevelChanged?.Invoke(this, level);
    }

    static double DbToLinear(float db)
    {
        if (float.IsNegativeInfinity(db) || db <= -60f)
            return 0.0;
        if (db >= 0f)
            return 1.0;
        return Math.Pow(10.0, db / 20.0);
    }

    public Task StopAsync() => this.playbacks.StopAllAsync();

    public async ValueTask DisposeAsync()
    {
        await this.playbacks.StopAllAsync().ConfigureAwait(false);

#if MACOS
        this.macVolume?.Dispose();
        this.macVolume = null;
#else
        this.volumeObserver?.Dispose();   // removes the KVO registration
        this.volumeObserver = null;
#endif
    }
}
