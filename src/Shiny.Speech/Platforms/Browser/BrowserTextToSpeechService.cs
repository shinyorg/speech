using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices.JavaScript;

namespace Shiny.Speech;

[SupportedOSPlatform("browser")]
public partial class BrowserTextToSpeechService : ITextToSpeechService
{
    static TaskCompletionSource? activeSpeakTcs;

    readonly ILogger<BrowserTextToSpeechService> logger;

    public BrowserTextToSpeechService(ILogger<BrowserTextToSpeechService> logger)
    {
        this.logger = logger;
    }

    public bool IsSupported => BrowserJsModule.ImportAsync().IsCompletedSuccessfully && IsSynthesisSupported();
    public bool IsSpeaking => BrowserJsModule.ImportAsync().IsCompletedSuccessfully && GetIsSpeaking();

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CultureInfo? culture = null, CancellationToken cancellationToken = default)
    {
        await BrowserJsModule.ImportAsync();

        var voicesJson = GetVoicesJson(culture?.Name ?? "");
        var results = new List<VoiceInfo>();

        if (!string.IsNullOrEmpty(voicesJson))
        {
            // voicesJson is a simple pipe-delimited format: "id|name|lang;id|name|lang;..."
            foreach (var entry in voicesJson.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('|');
                if (parts.Length >= 3)
                {
                    try
                    {
                        var voiceCulture = new CultureInfo(parts[2]);
                        results.Add(new VoiceInfo(parts[0], parts[1], voiceCulture));
                    }
                    catch
                    {
                        // Skip voices with unparseable culture
                    }
                }
            }
        }

        return results;
    }

    public async Task SpeakAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        await BrowserJsModule.ImportAsync();
        if (!IsSynthesisSupported())
            return;

        activeSpeakTcs?.TrySetResult();
        activeSpeakTcs = new TaskCompletionSource();
        options ??= new TextToSpeechOptions();

        var lang = options.Culture?.Name ?? "";
        var voiceUri = options.Voice?.Id ?? "";

        Speak(text, lang, voiceUri, options.SpeechRate, options.Pitch, options.Volume);
        logger.LogDebug("Browser TTS started");

        cancellationToken.Register(() =>
        {
            CancelSpeech();
            activeSpeakTcs?.TrySetResult();
        });

        await activeSpeakTcs.Task;
    }

    public Task StopAsync()
    {
        CancelSpeech();
        activeSpeakTcs?.TrySetResult();
        activeSpeakTcs = null;
        logger.LogDebug("Browser TTS stopped");
        return Task.CompletedTask;
    }

    [JSImport("shinySpeech.isSynthesisSupported", "shiny-speech")]
    private static partial bool IsSynthesisSupported();

    [JSImport("shinySpeech.getIsSpeaking", "shiny-speech")]
    private static partial bool GetIsSpeaking();

    [JSImport("shinySpeech.getVoices", "shiny-speech")]
    private static partial string GetVoicesJson(string cultureFilter);

    [JSImport("shinySpeech.speak", "shiny-speech")]
    private static partial void Speak(string text, string lang, string voiceUri, float rate, float pitch, float volume);

    [JSImport("shinySpeech.cancelSpeech", "shiny-speech")]
    private static partial void CancelSpeech();

    [JSExport]
    public static void OnSpeakEnd()
    {
        activeSpeakTcs?.TrySetResult();
        activeSpeakTcs = null;
    }
}
