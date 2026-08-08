using Microsoft.Extensions.Logging;
using Shiny.Audio.Infrastructure;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Shiny.Audio;

public class WindowsAudioPlayer(ILogger<WindowsAudioPlayer> logger) : IAudioPlayer
{
    readonly AudioPlaybackRegistry playbacks = new();

    WindowsSystemVolume? systemVolume;

    public bool IsPlaying => this.playbacks.IsPlaying;
    public IReadOnlyList<IAudioPlayback> Active => this.playbacks.Active;
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

    public async Task<IAudioPlayback> StartAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        var ras = new InMemoryRandomAccessStream();
        await audioStream.CopyToAsync(ras.AsStreamForWrite(), cancellationToken);
        ras.Seek(0);

        return StartCore(MediaSource.CreateFromStream(ras, "audio/mpeg"), null, cancellationToken);
    }

    public Task<IAudioPlayback> StartAsync(string source, CancellationToken cancellationToken = default)
    {
        // MediaSource.CreateFromUri handles both remote http(s) URLs and local file:// paths.
        var uri = PlaybackSource.IsRemote(source) ? new Uri(source) : new Uri(Path.GetFullPath(source));
        return Task.FromResult(StartCore(MediaSource.CreateFromUri(uri), source, cancellationToken));
    }

    IAudioPlayback StartCore(IMediaPlaybackSource mediaSource, string? source, CancellationToken cancellationToken)
    {
        // One MediaPlayer per clip — Windows mixes them itself, so overlapping clips just work.
        var mediaPlayer = new MediaPlayer { Source = mediaSource };
        var playback = this.playbacks.Create(source);

        void OnMediaEnded(MediaPlayer sender, object args) => playback.Complete();
        void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            logger.LogWarning("Windows audio playback failed: {Error}", args.ErrorMessage);
            playback.Fail(new InvalidOperationException($"Audio playback failed: {args.ErrorMessage}"));
        }

        mediaPlayer.MediaEnded += OnMediaEnded;
        mediaPlayer.MediaFailed += OnMediaFailed;

        playback.OnStop(() =>
        {
            mediaPlayer.MediaEnded -= OnMediaEnded;
            mediaPlayer.MediaFailed -= OnMediaFailed;
            mediaPlayer.Pause();
            mediaPlayer.Dispose();
            logger.LogDebug("Windows audio playback stopped ({Source})", source);
            return Task.CompletedTask;
        });

        mediaPlayer.Play();

        // Linked last so an already-cancelled token tears down a fully constructed playback.
        playback.CancelWith(cancellationToken);
        logger.LogDebug("Windows audio playback started ({Source})", source);
        return playback;
    }

    public Task StopAsync() => this.playbacks.StopAllAsync();

    public async ValueTask DisposeAsync()
    {
        await this.playbacks.StopAllAsync().ConfigureAwait(false);
        this.systemVolume?.Dispose();
        this.systemVolume = null;
    }
}
