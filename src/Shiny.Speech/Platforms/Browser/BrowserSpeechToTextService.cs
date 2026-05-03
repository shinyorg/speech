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

    public bool IsSupported => BrowserJsModule.ImportAsync().IsCompletedSuccessfully && IsRecognitionSupported();

    public async Task<AccessState> RequestAccess()
    {
        await BrowserJsModule.ImportAsync();
        if (!IsRecognitionSupported())
            return AccessState.NotSupported;

        var result = await RequestMicrophoneAccess();
        return result switch
        {
            "available" => AccessState.Available,
            "denied" => AccessState.Denied,
            "not-supported" => AccessState.NotSupported,
            "network" => AccessState.Restricted,
            _ => AccessState.Unknown
        };
    }

    public async IAsyncEnumerable<SpeechRecognitionResult> ContinuousRecognize(
        SpeechRecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await BrowserJsModule.ImportAsync();
        if (!IsRecognitionSupported())
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
            await StartRecognitionAsync(lang, true);
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
        await BrowserJsModule.ImportAsync();
        if (!IsRecognitionSupported())
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
            await StartRecognitionAsync(lang, false);
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

    [JSImport("shinySpeech.requestMicrophoneAccess", "shiny-speech")]
    private static partial Task<string> RequestMicrophoneAccess();

    [JSImport("shinySpeech.startRecognition", "shiny-speech")]
    private static partial Task StartRecognitionAsync(string lang, bool continuous);

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
        activeChannel?.Writer.TryComplete(new InvalidOperationException($"Speech recognition error: {error}"));
    }
}
