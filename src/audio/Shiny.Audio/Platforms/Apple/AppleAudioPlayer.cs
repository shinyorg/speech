using AVFoundation;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

public class AppleAudioPlayer(ILogger<AppleAudioPlayer> logger) : IAudioPlayer
{
    AVAudioPlayer? player;
    TaskCompletionSource? playbackTcs;
    NSTimer? meterTimer;

    public bool IsPlaying => player?.Playing ?? false;
    public bool IsPlayerAnalysisSupported => true;
    public event EventHandler<double>? AudioLevelChanged;

    public async Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, cancellationToken);

        var newPlayer = AVAudioPlayer.FromData(NSData.FromArray(ms.ToArray()));
        if (newPlayer == null)
            throw new InvalidOperationException("Failed to create audio player from data");

        await PlayCoreAsync(newPlayer, cancellationToken);
    }

    public async Task PlayAsync(string source, CancellationToken cancellationToken = default)
    {
        AVAudioPlayer? newPlayer;
        if (PlaybackSource.IsRemote(source))
        {
            // AVAudioPlayer cannot stream a remote URL, so buffer it into memory first
            // (keeps metering working, same as the stream path).
            var bytes = await PlaybackSource.DownloadAsync(source, cancellationToken);
            newPlayer = AVAudioPlayer.FromData(NSData.FromArray(bytes));
        }
        else
        {
            newPlayer = AVAudioPlayer.FromUrl(NSUrl.FromFilename(source), out _);
        }

        if (newPlayer == null)
            throw new InvalidOperationException($"Failed to create audio player from source: {source}");

        await PlayCoreAsync(newPlayer, cancellationToken);
    }

    async Task PlayCoreAsync(AVAudioPlayer newPlayer, CancellationToken cancellationToken)
    {
        await StopAsync();

        player = newPlayer;
        player.MeteringEnabled = true;

#if !MACOS
        // If something else (e.g. an active STT session) has already configured PlayAndRecord,
        // leave it alone. Switching to Playback-only would suspend the microphone and break any
        // concurrent recognition. Always reactivate the session in case it was deactivated.
        // Preserve the current category options so we don't tear down another component's ducking
        // (e.g. Shiny.Music's Duck() sets Playback + DuckOthers and expects this playback to be heard
        // over the ducked music). We do NOT force DefaultToSpeaker: it's a no-op for plain Playback
        // (which already defaults to the main speaker) and pins output local, preventing playback from
        // following the system output route to headphones / Bluetooth / AirPlay (HomePod).
        var session = AVAudioSession.SharedInstance();
        var playAndRecord = AVAudioSessionCategory.PlayAndRecord.GetConstant();
        if (session.Category != playAndRecord)
            session.SetCategory(AVAudioSessionCategory.Playback, session.CategoryOptions, out _);
        session.SetActive(true, out _);
#endif

        // RunContinuationsAsynchronously is critical: OnFinishedPlaying runs on AVAudioPlayer's native
        // FinishedPlaying callback. Without this, the await below resumes inline on that callback stack,
        // and the downstream code (e.g. the next StopAsync) would Dispose() this player while the native
        // callback is still executing — which the runtime reports as "player object was Dispose()d during
        // the callback ... corrupted the state of the program". Async continuations unwind the native
        // callback first, so disposal always happens on a clean stack.
        playbackTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.FinishedPlaying += OnFinishedPlaying;

        using var reg = cancellationToken.Register(() =>
        {
            player?.Stop();
            playbackTcs?.TrySetResult();
        });

        player.Play();
        StartMeterTimer();
        logger.LogDebug("Apple audio playback started");

        await playbackTcs.Task;
        logger.LogDebug("Apple audio playback finished");

        StopMeterTimer();
        // Only clean up if StopAsync/DisposeAsync hasn't already torn this player down (it nulls the
        // field and detaches/disposes). Touching a disposed player here would itself throw.
        if (ReferenceEquals(player, newPlayer))
            newPlayer.FinishedPlaying -= OnFinishedPlaying;
    }

    void StartMeterTimer()
    {
        StopMeterTimer();
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            meterTimer = NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromMilliseconds(50), _ => SampleMeter());
        });
    }

    void StopMeterTimer()
    {
        if (meterTimer != null)
        {
            var t = meterTimer;
            meterTimer = null;
            DispatchQueue.MainQueue.DispatchAsync(t.Invalidate);
        }
    }

    void SampleMeter()
    {
        var p = player;
        if (p == null || !p.Playing)
            return;

        p.UpdateMeters();
        var db = p.AveragePower(0);
        var level = DbToLinear(db);
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

    void OnFinishedPlaying(object? sender, AVStatusEventArgs e)
        => playbackTcs?.TrySetResult();

    public Task StopAsync()
    {
        StopMeterTimer();
        var p = player;
        if (p != null)
        {
            player = null;
            p.FinishedPlaying -= OnFinishedPlaying;
            p.Stop();
            p.Dispose();
            playbackTcs?.TrySetResult();
            logger.LogDebug("Apple audio playback stopped");
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        StopMeterTimer();
        var p = player;
        if (p != null)
        {
            player = null;
            p.FinishedPlaying -= OnFinishedPlaying;
            p.Stop();
            p.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
