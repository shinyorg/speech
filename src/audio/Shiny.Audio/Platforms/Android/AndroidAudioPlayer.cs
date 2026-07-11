using Android.Content;
using Android.Media;
using Android.Media.Audiofx;
using Android.Provider;
using Microsoft.Extensions.Logging;
using Stream = System.IO.Stream;
using AudioStream = Android.Media.Stream;

namespace Shiny.Audio;

public class AndroidAudioPlayer(ILogger<AndroidAudioPlayer> logger) : IAudioPlayer
{
    MediaPlayer? mediaPlayer;
    Visualizer? visualizer;
    TaskCompletionSource? playbackTcs;

    AudioManager? audioManager;
    VolumeObserver? volumeObserver;
    int lastVolumeStep = -1;

    public bool IsPlaying => mediaPlayer?.IsPlaying ?? false;
    public bool IsPlayerAnalysisSupported => true;
    public event EventHandler<double>? AudioLevelChanged;

    AudioManager AudioManager => this.audioManager ??=
        (AudioManager)Android.App.Application.Context.GetSystemService(Context.AudioService)!;

    // Volume maps to the device-wide STREAM_MUSIC level (the one the hardware buttons move) — NOT any
    // per-utterance TTS volume. Reading and setting are both supported on Android.
    public bool IsVolumeControlSupported => true;

    public float Volume
    {
        get
        {
            var max = this.AudioManager.GetStreamMaxVolume(AudioStream.Music);
            return max <= 0 ? 0f : this.AudioManager.GetStreamVolume(AudioStream.Music) / (float)max;
        }
        set
        {
            var max = this.AudioManager.GetStreamMaxVolume(AudioStream.Music);
            var step = (int)Math.Round(Math.Clamp(value, 0f, 1f) * max);
            // Flags 0 = change silently, without popping the system volume UI. This triggers the content
            // observer, which raises VolumeChanged.
            this.AudioManager.SetStreamVolume(AudioStream.Music, step, (VolumeNotificationFlags)0);
        }
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

    // Android has no KVO; watch the system settings URI and filter to actual STREAM_MUSIC changes.
    // Registered lazily on first subscription and torn down in DisposeAsync.
    void EnsureVolumeObserver()
    {
        if (this.volumeObserver != null)
            return;

        this.lastVolumeStep = this.AudioManager.GetStreamVolume(AudioStream.Music);
        this.volumeObserver = new VolumeObserver(this.OnSystemVolumeChanged);
        Android.App.Application.Context.ContentResolver!.RegisterContentObserver(
            Settings.System.ContentUri!, true, this.volumeObserver);
    }

    void OnSystemVolumeChanged()
    {
        var step = this.AudioManager.GetStreamVolume(AudioStream.Music);
        if (step == this.lastVolumeStep)
            return;   // the observer fires for any system setting; ignore non-music-volume changes

        this.lastVolumeStep = step;
        var max = this.AudioManager.GetStreamMaxVolume(AudioStream.Music);
        this.volumeChanged?.Invoke(this, max <= 0 ? 0f : step / (float)max);
    }

    public async Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        await StopAsync();

        var header = new byte[16];
        var headerRead = 0;
        while (headerRead < header.Length)
        {
            var n = await audioStream.ReadAsync(header.AsMemory(headerRead, header.Length - headerRead), cancellationToken);
            if (n == 0)
                break;
            headerRead += n;
        }

        var extension = SniffExtension(header, headerRead);
        var tempFile = Path.Combine(
            Android.App.Application.Context.CacheDir!.AbsolutePath,
            $"tts_{Guid.NewGuid()}{extension}"
        );

        try
        {
            await using (var fs = File.Create(tempFile))
            {
                if (headerRead > 0)
                    await fs.WriteAsync(header.AsMemory(0, headerRead), cancellationToken);
                await audioStream.CopyToAsync(fs, cancellationToken);
            }

            mediaPlayer = new MediaPlayer();
            playbackTcs = new TaskCompletionSource();

            mediaPlayer.Completion += OnCompletion;
            mediaPlayer.Error += OnError;

            await mediaPlayer.SetDataSourceAsync(tempFile);
            mediaPlayer.Prepare();

            using var reg = cancellationToken.Register(() =>
            {
                mediaPlayer?.Stop();
                playbackTcs?.TrySetResult();
            });

            mediaPlayer.Start();
            AttachVisualizer(mediaPlayer.AudioSessionId);
            logger.LogDebug("Android audio playback started");

            await playbackTcs.Task;
            logger.LogDebug("Android audio playback finished");
        }
        finally
        {
            DetachVisualizer();
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    public async Task PlayAsync(string source, CancellationToken cancellationToken = default)
    {
        await StopAsync();

        mediaPlayer = new MediaPlayer();
        playbackTcs = new TaskCompletionSource();
        mediaPlayer.Completion += OnCompletion;
        mediaPlayer.Error += OnError;

        try
        {
            // MediaPlayer resolves both remote http(s) URLs and local file paths natively.
            await mediaPlayer.SetDataSourceAsync(source);

            // PrepareAsync (not the blocking Prepare) so remote sources buffer off the calling thread.
            var prepared = new TaskCompletionSource();
            void OnPrepared(object? sender, EventArgs e) => prepared.TrySetResult();
            void OnPrepareError(object? sender, MediaPlayer.ErrorEventArgs e)
                => prepared.TrySetException(new InvalidOperationException($"Audio playback error: {e.What}"));

            mediaPlayer.Prepared += OnPrepared;
            mediaPlayer.Error += OnPrepareError;
            mediaPlayer.PrepareAsync();

            using var reg = cancellationToken.Register(() =>
            {
                try { mediaPlayer?.Stop(); } catch { /* player may not be started */ }
                prepared.TrySetResult();
                playbackTcs?.TrySetResult();
            });

            await prepared.Task;
            mediaPlayer.Prepared -= OnPrepared;
            mediaPlayer.Error -= OnPrepareError;

            if (cancellationToken.IsCancellationRequested)
                return;

            mediaPlayer.Start();
            AttachVisualizer(mediaPlayer.AudioSessionId);
            logger.LogDebug("Android audio playback started ({Source})", source);

            await playbackTcs.Task;
            logger.LogDebug("Android audio playback finished");
        }
        finally
        {
            DetachVisualizer();
        }
    }

    void AttachVisualizer(int sessionId)
    {
        DetachVisualizer();
        try
        {
            visualizer = new Visualizer(sessionId);
            var sizeRange = Visualizer.GetCaptureSizeRange();
            if (sizeRange != null && sizeRange.Length > 0)
                visualizer.SetCaptureSize(sizeRange[0]);

            var listener = new WaveformListener(level => AudioLevelChanged?.Invoke(this, level));
            visualizer.SetDataCaptureListener(listener, Visualizer.MaxCaptureRate / 2, true, false);
            visualizer.SetEnabled(true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to attach Android Visualizer; audio levels will not be emitted");
            visualizer?.Release();
            visualizer = null;
        }
    }

    void DetachVisualizer()
    {
        if (visualizer != null)
        {
            try
            {
                visualizer.SetEnabled(false);
                visualizer.Release();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error releasing Android Visualizer");
            }
            visualizer = null;
        }
    }

    static string SniffExtension(byte[] header, int length)
    {
        if (length >= 12 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
            return ".m4a";

        if (length >= 4 && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F')
            return ".wav";

        if (length >= 4 && header[0] == (byte)'O' && header[1] == (byte)'g' && header[2] == (byte)'g' && header[3] == (byte)'S')
            return ".ogg";

        if (length >= 4 && header[0] == (byte)'f' && header[1] == (byte)'L' && header[2] == (byte)'a' && header[3] == (byte)'C')
            return ".flac";

        return ".mp3";
    }

    void OnCompletion(object? sender, EventArgs e)
        => playbackTcs?.TrySetResult();

    void OnError(object? sender, MediaPlayer.ErrorEventArgs e)
    {
        logger.LogWarning("Android audio playback error: {What} {Extra}", e.What, e.Extra);
        playbackTcs?.TrySetException(new InvalidOperationException($"Audio playback error: {e.What}"));
    }

    public Task StopAsync()
    {
        DetachVisualizer();
        if (mediaPlayer != null)
        {
            if (mediaPlayer.IsPlaying)
                mediaPlayer.Stop();

            mediaPlayer.Release();
            mediaPlayer.Dispose();
            mediaPlayer = null;
            playbackTcs?.TrySetResult();
            logger.LogDebug("Android audio playback stopped");
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DetachVisualizer();
        mediaPlayer?.Release();
        mediaPlayer?.Dispose();
        mediaPlayer = null;

        if (this.volumeObserver != null)
        {
            Android.App.Application.Context.ContentResolver!.UnregisterContentObserver(this.volumeObserver);
            this.volumeObserver.Dispose();
            this.volumeObserver = null;
        }
        return ValueTask.CompletedTask;
    }

    // Fires OnChange on the main looper whenever a system setting changes; OnSystemVolumeChanged filters to
    // actual STREAM_MUSIC volume changes.
    sealed class VolumeObserver(Action onChanged)
        : Android.Database.ContentObserver(new Android.OS.Handler(Android.OS.Looper.MainLooper!))
    {
        public override void OnChange(bool selfChange) => onChanged();
    }

    sealed class WaveformListener(Action<double> onLevel) : Java.Lang.Object, Visualizer.IOnDataCaptureListener
    {
        public void OnFftDataCapture(Visualizer? visualizer, byte[]? fft, int samplingRate)
        {
        }

        public void OnWaveFormDataCapture(Visualizer? visualizer, byte[]? waveform, int samplingRate)
        {
            if (waveform == null || waveform.Length == 0)
                return;

            // Visualizer waveform is 8-bit unsigned PCM centered at 128.
            double sumSquares = 0;
            for (var i = 0; i < waveform.Length; i++)
            {
                var s = (waveform[i] - 128) / 128.0;
                sumSquares += s * s;
            }
            var rms = Math.Sqrt(sumSquares / waveform.Length);
            onLevel(Math.Clamp(rms, 0.0, 1.0));
        }
    }
}
