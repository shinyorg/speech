using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Shiny.Audio;

public class WindowsAudioPlayer(ILogger<WindowsAudioPlayer> logger) : IAudioPlayer
{
    MediaPlayer? mediaPlayer;
    TaskCompletionSource? playbackTcs;

    WindowsSystemVolume? systemVolume;

    public bool IsPlaying => mediaPlayer?.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing;
    public bool IsPlayerAnalysisSupported => false;
#pragma warning disable CS0067
    public event EventHandler<double>? AudioLevelChanged;
#pragma warning restore CS0067

    // System (default render endpoint) volume via WASAPI IAudioEndpointVolume — settable, with change
    // notifications. Created lazily so apps that never touch volume pay nothing.
    WindowsSystemVolume SystemVolume => this.systemVolume ??= CreateSystemVolume();

    WindowsSystemVolume CreateSystemVolume()
    {
        var sv = new WindowsSystemVolume();
        sv.Changed += v => this.volumeChanged?.Invoke(this, v);
        return sv;
    }

    public bool IsVolumeControlSupported => this.SystemVolume.CanSet;
    public float Volume
    {
        get => this.SystemVolume.Get();
        set => this.SystemVolume.Set(value);   // the endpoint callback raises VolumeChanged
    }

    event EventHandler<float>? volumeChanged;
    public event EventHandler<float>? VolumeChanged
    {
        add
        {
            this.volumeChanged += value;
            _ = this.SystemVolume;   // ensure the endpoint callback is registered
        }
        remove => this.volumeChanged -= value;
    }

    public async Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        var ras = new InMemoryRandomAccessStream();
        await audioStream.CopyToAsync(ras.AsStreamForWrite(), cancellationToken);
        ras.Seek(0);

        await PlayCoreAsync(MediaSource.CreateFromStream(ras, "audio/mpeg"), cancellationToken);
    }

    public Task PlayAsync(string source, CancellationToken cancellationToken = default)
    {
        // MediaSource.CreateFromUri handles both remote http(s) URLs and local file:// paths.
        var uri = PlaybackSource.IsRemote(source) ? new Uri(source) : new Uri(Path.GetFullPath(source));
        return PlayCoreAsync(MediaSource.CreateFromUri(uri), cancellationToken);
    }

    async Task PlayCoreAsync(IMediaPlaybackSource source, CancellationToken cancellationToken)
    {
        await StopAsync();

        mediaPlayer = new MediaPlayer();
        mediaPlayer.Source = source;

        playbackTcs = new TaskCompletionSource();
        mediaPlayer.MediaEnded += OnMediaEnded;
        mediaPlayer.MediaFailed += OnMediaFailed;

        using var reg = cancellationToken.Register(() =>
        {
            mediaPlayer?.Pause();
            playbackTcs?.TrySetResult();
        });

        mediaPlayer.Play();
        logger.LogDebug("Windows audio playback started");

        await playbackTcs.Task;
        logger.LogDebug("Windows audio playback finished");
    }

    void OnMediaEnded(MediaPlayer sender, object args)
        => playbackTcs?.TrySetResult();

    void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        logger.LogWarning("Windows audio playback failed: {Error}", args.ErrorMessage);
        playbackTcs?.TrySetException(new InvalidOperationException($"Audio playback failed: {args.ErrorMessage}"));
    }

    public Task StopAsync()
    {
        if (mediaPlayer != null)
        {
            mediaPlayer.Pause();
            mediaPlayer.Dispose();
            mediaPlayer = null;
            playbackTcs?.TrySetResult();
            logger.LogDebug("Windows audio playback stopped");
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        mediaPlayer?.Dispose();
        mediaPlayer = null;
        this.systemVolume?.Dispose();
        this.systemVolume = null;
        return ValueTask.CompletedTask;
    }
}
