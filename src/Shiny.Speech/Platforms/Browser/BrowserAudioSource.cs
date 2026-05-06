using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Shiny.Speech;

[SupportedOSPlatform("browser")]
public partial class BrowserAudioSource(ILogger<BrowserAudioSource> logger) : IAudioSource
{
    static PipeStream? activePipe;

    public async Task<Stream> StartCaptureAsync(CancellationToken cancellationToken = default)
    {
        await BrowserJsModule.ImportAsync();

        var pipe = new PipeStream();
        activePipe = pipe;

        await StartMicrophoneCaptureAsync();
        logger.LogDebug("Browser audio capture started");

        return pipe;
    }

    public Task StopCaptureAsync()
    {
        StopMicrophoneCapture();
        activePipe?.Dispose();
        activePipe = null;
        logger.LogDebug("Browser audio capture stopped");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        GC.SuppressFinalize(this);
    }

    [JSImport("shinySpeech.startMicrophoneCapture", "shiny-speech")]
    private static partial Task StartMicrophoneCaptureAsync();

    [JSImport("shinySpeech.stopMicrophoneCapture", "shiny-speech")]
    private static partial void StopMicrophoneCapture();

    [JSExport]
    public static void OnAudioData(byte[] pcmData)
    {
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
    }
}
