using System.Globalization;
using Microsoft.Extensions.Logging;
using OpenAI.Audio;
using Shiny.Speech.Cloud;

namespace Shiny.Speech.OpenAI;

public class OpenAiTextToSpeechProvider(
    OpenAiSpeechConfig config,
    ILogger<OpenAiTextToSpeechProvider> logger
) : ITextToSpeechProvider
{
    static readonly IReadOnlyList<VoiceInfo> AvailableVoices =
    [
        new("alloy", "Alloy", CultureInfo.InvariantCulture),
        new("ash", "Ash", CultureInfo.InvariantCulture),
        new("ballad", "Ballad", CultureInfo.InvariantCulture),
        new("coral", "Coral", CultureInfo.InvariantCulture),
        new("echo", "Echo", CultureInfo.InvariantCulture),
        new("fable", "Fable", CultureInfo.InvariantCulture),
        new("onyx", "Onyx", CultureInfo.InvariantCulture),
        new("nova", "Nova", CultureInfo.InvariantCulture),
        new("sage", "Sage", CultureInfo.InvariantCulture),
        new("shimmer", "Shimmer", CultureInfo.InvariantCulture)
    ];

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(
        CultureInfo? culture = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AvailableVoices);

    public async Task<Stream> SynthesizeAsync(
        string text,
        TextToSpeechOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var client = new AudioClient(config.TextToSpeechModel, config.ApiKey);

        var voiceId = options?.Voice?.Id ?? config.DefaultVoice;
        var voice = new GeneratedSpeechVoice(voiceId);

        var speechOptions = new SpeechGenerationOptions();
        if (options != null)
            speechOptions.SpeedRatio = options.SpeechRate;

        logger.LogDebug("Generating speech via OpenAI using voice {Voice}", voiceId);

        var result = await client.GenerateSpeechAsync(text, voice, speechOptions, cancellationToken);

        logger.LogDebug("OpenAI TTS completed, {Bytes} bytes", result.Value.ToArray().Length);
        return result.Value.ToStream();
    }
}
