using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

[SupportedOSPlatform("browser")]
public partial class BrowserAudioSource(ILogger<BrowserAudioSource> logger) : IAudioSource
{
    static PipeStream? activePipe;
    // The JS interop callbacks are static, so the capturing instance (and its throttle) has to be
    // reachable statically to raise the level event. Capture is single-session per app anyway.
    static BrowserAudioSource? activeSource;
    static AudioLevelThrottle? levelThrottle;

    public event EventHandler<double>? InputLevelChanged;

    public async Task<Stream> StartCaptureAsync(AudioProcessingOptions? processing = null, CancellationToken cancellationToken = default)
    {
        await BrowserJsModule.ImportAsync();

        var pipe = new PipeStream();
        activePipe = pipe;
        activeSource = this;
        levelThrottle = new AudioLevelThrottle();

        await StartMicrophoneCaptureAsync(
            processing?.EchoCancellation ?? false,
            processing?.NoiseSuppression ?? false,
            processing?.AutomaticGainControl ?? false
        );
        logger.LogDebug("Browser audio capture started");

        return pipe;
    }

    public Task StopCaptureAsync()
    {
        StopMicrophoneCapture();
        activePipe?.Dispose();
        activePipe = null;
        levelThrottle = null;
        if (ReferenceEquals(activeSource, this))
            activeSource = null;
        logger.LogDebug("Browser audio capture stopped");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        GC.SuppressFinalize(this);
    }

    [JSImport("shinySpeech.startMicrophoneCapture", "shiny-speech")]
    private static partial Task StartMicrophoneCaptureAsync(
        bool echoCancellation,
        bool noiseSuppression,
        bool autoGainControl
    );

    [JSImport("shinySpeech.stopMicrophoneCapture", "shiny-speech")]
    private static partial void StopMicrophoneCapture();

    [JSExport]
    public static void OnAudioData(byte[] pcmData)
    {
        var source = activeSource;
        var throttle = levelThrottle;
        if (source != null && throttle != null && throttle.TryEmit(AudioLevel.FromPcm16(pcmData), out var level))
            source.InputLevelChanged?.Invoke(source, level);

        try
        {
            activePipe?.Write(pcmData, 0, pcmData.Length);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [JSExport]
    public static void OnCaptureError(string error)
    {
        activePipe?.Dispose();
        activePipe = null;
        activeSource = null;
        levelThrottle = null;
    }
}
