using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices.JavaScript;
using Shiny.Audio.Infrastructure;

namespace Shiny.Audio;

[SupportedOSPlatform("browser")]
public partial class BrowserAudioPlayer : IAudioPlayer
{
    // Singleton bridge for the static [JSExport] callbacks (mirrors BrowserAudioSource).
    static BrowserAudioPlayer? current;

    readonly ILogger<BrowserAudioPlayer> logger;
    readonly AudioPlaybackRegistry playbacks = new();

    // JS owns one <audio> element per clip, keyed by the playback id, so the ended/error callbacks
    // can be routed back to the right handle.
    readonly ConcurrentDictionary<string, AudioPlayback> byElementId = new();

    public BrowserAudioPlayer(ILogger<BrowserAudioPlayer> logger)
    {
        this.logger = logger;
        current = this;
    }

    public bool IsPlaying => this.playbacks.IsPlaying;
    public IReadOnlyList<IAudioPlayback> Active => this.playbacks.Active;
    public bool IsPlayerAnalysisSupported => false;
#pragma warning disable CS0067
    public event EventHandler<double>? AudioLevelChanged;
#pragma warning restore CS0067

    // Browsers sandbox the OS volume, so "Volume" here is the app's own media-element volume (0.0–1.0):
    // settable and readable, and it persists across plays (applied to each new <audio> element).
    public bool IsVolumeControlSupported => true;

    public float Volume
    {
        get => BrowserJsModule.ImportAsync().IsCompletedSuccessfully ? GetVolume() : 1f;
        set
        {
            _ = BrowserJsModule.ImportAsync();
            SetVolume(Math.Clamp(value, 0f, 1f));   // JS echoes back via OnVolumeChanged
        }
    }

    public event EventHandler<float>? VolumeChanged;

    public async Task<IAudioPlayback> StartAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        await BrowserJsModule.ImportAsync();

        // Convert the stream to a base64 data URL for the browser Audio API
        using var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, cancellationToken);
        var base64 = Convert.ToBase64String(ms.ToArray());

        return this.StartCore($"data:audio/mp3;base64,{base64}", null, cancellationToken);
    }

    public async Task<IAudioPlayback> StartAsync(string source, CancellationToken cancellationToken = default)
    {
        await BrowserJsModule.ImportAsync();

        // The browser Audio element loads remote URLs (and app-relative paths) directly.
        return this.StartCore(source, source, cancellationToken);
    }

    IAudioPlayback StartCore(string url, string? source, CancellationToken cancellationToken)
    {
        var playback = this.playbacks.Create(source);
        var elementId = playback.Id.ToString();
        this.byElementId[elementId] = playback;

        playback.OnStop(() =>
        {
            this.byElementId.TryRemove(elementId, out _);
            StopAudio(elementId);
            this.logger.LogDebug("Browser audio playback stopped ({Source})", source);
            return Task.CompletedTask;
        });

        PlayAudio(elementId, url);

        // Linked last so an already-cancelled token tears down a fully constructed playback.
        playback.CancelWith(cancellationToken);
        this.logger.LogDebug("Browser audio playback started ({Source})", source);
        return playback;
    }

    public Task StopAsync() => this.playbacks.StopAllAsync();

    public ValueTask DisposeAsync() => new(this.playbacks.StopAllAsync());

    [JSImport("shinySpeech.playAudio", "shiny-speech")]
    private static partial void PlayAudio(string id, string url);

    [JSImport("shinySpeech.stopAudio", "shiny-speech")]
    private static partial void StopAudio(string id);

    [JSImport("shinySpeech.getVolume", "shiny-speech")]
    private static partial float GetVolume();

    [JSImport("shinySpeech.setVolume", "shiny-speech")]
    private static partial void SetVolume(float volume);

    [JSExport]
    public static void OnVolumeChanged(float volume)
        => current?.VolumeChanged?.Invoke(current, volume);

    /// <summary>The &lt;audio&gt; element reached its end. Called from JS — not part of the public API.</summary>
    [JSExport]
    public static void OnPlaybackEnded(string id)
    {
        if (current != null && current.byElementId.TryGetValue(id, out var playback))
            playback.Complete();
    }

    /// <summary>The &lt;audio&gt; element failed to load or decode. Called from JS — not part of the public API.</summary>
    [JSExport]
    public static void OnPlaybackFailed(string id, string message)
    {
        if (current != null && current.byElementId.TryGetValue(id, out var playback))
            playback.Fail(new InvalidOperationException($"Audio playback failed: {message}"));
    }
}
