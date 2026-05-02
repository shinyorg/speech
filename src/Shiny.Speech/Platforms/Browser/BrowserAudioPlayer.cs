using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices.JavaScript;

namespace Shiny.Speech;

[SupportedOSPlatform("browser")]
public partial class BrowserAudioPlayer(ILogger<BrowserAudioPlayer> logger) : IAudioPlayer
{
    TaskCompletionSource? playTcs;

    public bool IsPlaying => GetIsPlaying();

    public Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        playTcs?.TrySetResult();
        playTcs = new TaskCompletionSource();

        // Convert stream to base64 data URL for the browser Audio API
        using var ms = new MemoryStream();
        audioStream.CopyTo(ms);
        var base64 = Convert.ToBase64String(ms.ToArray());
        var dataUrl = $"data:audio/mp3;base64,{base64}";

        PlayAudio(dataUrl);
        logger.LogDebug("Browser audio playback started");

        cancellationToken.Register(() =>
        {
            StopAudio();
            playTcs?.TrySetResult();
        });

        return playTcs.Task;
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
}
