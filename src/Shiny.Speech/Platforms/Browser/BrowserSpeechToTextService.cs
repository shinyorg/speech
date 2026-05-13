using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices.JavaScript;

namespace Shiny.Speech;

[SupportedOSPlatform("browser")]
public partial class BrowserSpeechToTextService(ILogger<BrowserSpeechToTextService> logger) : ISpeechToTextService
{
    static BrowserSpeechToTextService? activeInstance;
    static Regex? keywordPattern;

    public bool IsSupported => BrowserJsModule.ImportAsync().IsCompletedSuccessfully && IsRecognitionSupported();
    public bool IsListening { get; private set; }

    public event EventHandler<SpeechRecognitionResult>? ResultReceived;
    public event EventHandler<string>? KeywordHeard;
    public event EventHandler<SpeechRecognitionError>? Error;

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

    public async Task Start(SpeechRecognitionOptions? options = null)
    {
        if (IsListening)
            throw new InvalidOperationException("Speech recognition is already active. Call Stop() before starting again.");

        await BrowserJsModule.ImportAsync();
        if (!IsRecognitionSupported())
            throw new InvalidOperationException("Speech recognition is not supported in this browser.");

        options ??= new SpeechRecognitionOptions();
        keywordPattern = BuildKeywordPattern(options.Keywords);
        activeInstance = this;

        var lang = options.Culture?.Name ?? "";
        await StartRecognitionAsync(lang, true);
        IsListening = true;
        logger.LogDebug("Browser speech recognition started");
    }

    public Task Stop()
    {
        if (!IsListening)
            return Task.CompletedTask;

        IsListening = false;
        keywordPattern = null;
        StopRecognition();
        activeInstance = null;
        logger.LogDebug("Browser speech recognition stopped");
        return Task.CompletedTask;
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
        var instance = activeInstance;
        if (instance == null)
            return;

        var result = new SpeechRecognitionResult(text, isFinal, confidence);
        instance.ResultReceived?.Invoke(instance, result);

        if (isFinal && keywordPattern != null)
        {
            var match = keywordPattern.Match(text);
            if (match.Success)
                instance.KeywordHeard?.Invoke(instance, match.Value);
        }
    }

    [JSExport]
    public static void OnEnd()
    {
        var instance = activeInstance;
        if (instance == null)
            return;

        instance.IsListening = false;
        activeInstance = null;
    }

    [JSExport]
    public static void OnError(string error)
    {
        var instance = activeInstance;
        if (instance == null)
            return;

        instance.Error?.Invoke(instance, new SpeechRecognitionError(
            error,
            new InvalidOperationException($"Speech recognition error: {error}")
        ));
    }

    static Regex? BuildKeywordPattern(string[]? keywords)
    {
        if (keywords == null || keywords.Length == 0)
            return null;

        return new Regex(
            @"\b(" + string.Join("|", keywords.Select(Regex.Escape)) + @")\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );
    }
}
