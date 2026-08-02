using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

[SupportedOSPlatform("browser")]
public partial class BrowserAudioSource(ILogger<BrowserAudioSource> logger) : IAudioSource
{
    // The JS interop callbacks are static, so the capturing session has to be reachable statically
    // to process buffers and raise the level event. Capture is single-session per app anyway.
    static CaptureSink? activeSink;
    static BrowserAudioSource? activeSource;

    public event EventHandler<double>? InputLevelChanged;

    public async Task<Stream> StartCaptureAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await BrowserJsModule.ImportAsync();

        var processing = options.Processing;
        activeSource = this;

        var sink = new CaptureSink(options, level => activeSource?.InputLevelChanged?.Invoke(activeSource, level));
        activeSink = sink;

        await StartMicrophoneCaptureAsync(
            processing?.EchoCancellation ?? false,
            processing?.NoiseSuppression ?? false,
            processing?.AutomaticGainControl ?? false
        );
        logger.LogDebug("Browser audio capture started");

        return sink.Stream;
    }

    public Task StopCaptureAsync()
    {
        StopMicrophoneCapture();
        activeSink?.Dispose();
        activeSink = null;
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
        => activeSink?.Write(pcmData, 0, pcmData.Length);

    [JSExport]
    public static void OnCaptureError(string error)
    {
        activeSink?.Dispose();
        activeSink = null;
        activeSource = null;
    }
}
