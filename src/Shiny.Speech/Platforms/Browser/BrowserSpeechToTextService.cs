using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices.JavaScript;

namespace Shiny.Speech;

[SupportedOSPlatform("browser")]
public partial class BrowserSpeechToTextService : ISpeechToTextService
{
    readonly ILogger<BrowserSpeechToTextService> logger;
    static Channel<SpeechRecognitionResult>? activeChannel;

    public BrowserSpeechToTextService(ILogger<BrowserSpeechToTextService> logger)
    {
        this.logger = logger;
    }

    public bool IsSupported => IsRecognitionSupported();

    public Task<AccessState> RequestAccess()
    {
        if (!IsSupported)
            return Task.FromResult(AccessState.NotSupported);

        return Task.FromResult(AccessState.Available);
    }

    public async IAsyncEnumerable<SpeechRecognitionResult> ContinuousRecognize(
        SpeechRecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            yield break;

        options ??= new SpeechRecognitionOptions();
        var lang = options.Culture?.Name ?? "";

        activeChannel = Channel.CreateUnbounded<SpeechRecognitionResult>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });

        try
        {
            StartRecognition(lang, true);
            logger.LogDebug("Browser speech recognition started (continuous)");

            using var reg = cancellationToken.Register(() =>
            {
                StopRecognition();
                activeChannel?.Writer.TryComplete();
            });

            await foreach (var result in activeChannel.Reader.ReadAllAsync(CancellationToken.None))
            {
                yield return result;
            }
        }
        finally
        {
            StopRecognition();
            activeChannel = null;
            logger.LogDebug("Browser speech recognition stopped");
        }
    }

    public async Task<string?> ListenUntilSilence(
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return null;

        options ??= new SpeechRecognitionOptions();
        var lang = options.Culture?.Name ?? "";

        activeChannel = Channel.CreateUnbounded<SpeechRecognitionResult>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });

        try
        {
            StartRecognition(lang, false);
            logger.LogDebug("Browser speech recognition started (single)");

            using var reg = cancellationToken.Register(() =>
            {
                StopRecognition();
                activeChannel?.Writer.TryComplete();
            });

            string? lastText = null;
            await foreach (var result in activeChannel.Reader.ReadAllAsync(CancellationToken.None))
            {
                lastText = result.Text;
                if (result.IsFinal)
                    return result.Text;
            }
            return lastText;
        }
        finally
        {
            StopRecognition();
            activeChannel = null;
            logger.LogDebug("Browser speech recognition stopped");
        }
    }

    [JSImport("shinySpeech.isRecognitionSupported", "shiny-speech")]
    private static partial bool IsRecognitionSupported();

    [JSImport("shinySpeech.startRecognition", "shiny-speech")]
    private static partial void StartRecognition(string lang, bool continuous);

    [JSImport("shinySpeech.stopRecognition", "shiny-speech")]
    private static partial void StopRecognition();

    [JSExport]
    public static void OnResult(string text, bool isFinal, float confidence)
    {
        activeChannel?.Writer.TryWrite(new SpeechRecognitionResult(text, isFinal, confidence));
    }

    [JSExport]
    public static void OnEnd()
    {
        activeChannel?.Writer.TryComplete();
    }

    [JSExport]
    public static void OnError(string error)
    {
        activeChannel?.Writer.TryComplete();
    }
}
