using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices.JavaScript;

namespace Shiny.Audio;

[SupportedOSPlatform("browser")]
public partial class BrowserAudioPlayer : IAudioPlayer
{
    // Singleton bridge for the static [JSExport] volume-change callback (mirrors BrowserAudioSource).
    static BrowserAudioPlayer? current;

    readonly ILogger<BrowserAudioPlayer> logger;
    TaskCompletionSource? playTcs;

    public BrowserAudioPlayer(ILogger<BrowserAudioPlayer> logger)
    {
        this.logger = logger;
        current = this;
    }

    public bool IsPlaying => BrowserJsModule.ImportAsync().IsCompletedSuccessfully && GetIsPlaying();
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

    public async Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        await BrowserJsModule.ImportAsync();
        playTcs?.TrySetResult();
        playTcs = new TaskCompletionSource();

        // Convert stream to base64 data URL for the browser Audio API
        using var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, cancellationToken);
        var base64 = Convert.ToBase64String(ms.ToArray());
        var dataUrl = $"data:audio/mp3;base64,{base64}";

        PlayAudio(dataUrl);
        logger.LogDebug("Browser audio playback started");

        cancellationToken.Register(() =>
        {
            StopAudio();
            playTcs?.TrySetResult();
        });

        await playTcs.Task;
    }

    public async Task PlayAsync(string source, CancellationToken cancellationToken = default)
    {
        await BrowserJsModule.ImportAsync();
        playTcs?.TrySetResult();
        playTcs = new TaskCompletionSource();

        // The browser Audio element loads remote URLs (and app-relative paths) directly.
        PlayAudio(source);
        logger.LogDebug("Browser audio playback started ({Source})", source);

        cancellationToken.Register(() =>
        {
            StopAudio();
            playTcs?.TrySetResult();
        });

        await playTcs.Task;
    }

    public Task StopAsync()
    {
        StopAudio();
        playTcs?.TrySetResult();
        playTcs = null;
        logger.LogDebug("Browser audio playback stopped");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        StopAudio();
        playTcs?.TrySetResult();
        return ValueTask.CompletedTask;
    }

    [JSImport("shinySpeech.getIsPlaying", "shiny-speech")]
    private static partial bool GetIsPlaying();

    [JSImport("shinySpeech.playAudio", "shiny-speech")]
    private static partial void PlayAudio(string dataUrl);

    [JSImport("shinySpeech.stopAudio", "shiny-speech")]
    private static partial void StopAudio();

    [JSImport("shinySpeech.getVolume", "shiny-speech")]
    private static partial float GetVolume();

    [JSImport("shinySpeech.setVolume", "shiny-speech")]
    private static partial void SetVolume(float volume);

    [JSExport]
    public static void OnVolumeChanged(float volume)
        => current?.VolumeChanged?.Invoke(current, volume);
}
