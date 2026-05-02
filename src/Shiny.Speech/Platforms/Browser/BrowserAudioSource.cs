using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Shiny.Speech;

[SupportedOSPlatform("browser")]
public class BrowserAudioSource(ILogger<BrowserAudioSource> logger) : IAudioSource
{
    public Task<Stream> StartCaptureAsync(CancellationToken cancellationToken = default)
    {
        logger.LogWarning("Browser audio capture is not supported — use cloud STT providers with browser-native recognition instead");
        throw new PlatformNotSupportedException("Raw audio capture is not supported in the browser. Use ISpeechToTextService for browser speech recognition.");
    }

    public Task StopCaptureAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
