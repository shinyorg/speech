using System.Globalization;
using Microsoft.Extensions.Logging;
using Shiny.Speech.Cloud;
using Typecast;
using Typecast.Models;

namespace Shiny.Speech.Typecast;

/// <summary>
/// <see cref="ITextToSpeechProvider"/> backed by the Typecast SDK (<c>typecast-csharp</c>).
/// Typecast is a text-to-speech service, so no speech-to-text provider is offered.
/// </summary>
public class TypecastTextToSpeechProvider(
    TypecastConfig config,
    ILogger<TypecastTextToSpeechProvider> logger
) : ITextToSpeechProvider, IDisposable
{
    // Rebuilds the underlying client if config.ApiKey is changed at runtime.
    readonly RefreshableClient<TypecastClient> client = new(() => new TypecastClient(config.ApiKey));

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(
        CultureInfo? culture = null,
        CancellationToken cancellationToken = default)
    {
        // Typecast voices are multilingual, so there is no per-voice culture to filter on —
        // all voices for the configured model are returned with the invariant culture.
        var filter = new VoicesV2Filter { Model = config.Model };
        var voices = await client.Get(config.ApiKey).GetVoicesV2Async(filter, cancellationToken);

        var result = voices
            .Where(v => !String.IsNullOrEmpty(v.VoiceId) && !String.IsNullOrEmpty(v.VoiceName))
            .Select(v => new VoiceInfo(v.VoiceId!, v.VoiceName!, CultureInfo.InvariantCulture))
            .ToList();

        logger.LogDebug("Typecast returned {Count} voices", result.Count);
        return result;
    }

    public async Task<Stream> SynthesizeAsync(
        string text,
        TextToSpeechOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TextToSpeechOptions();

        var voiceId = options.Voice?.Id ?? config.DefaultVoiceId;
        if (String.IsNullOrWhiteSpace(voiceId))
            throw new InvalidOperationException(
                "No Typecast voice specified. Set TypecastConfig.DefaultVoiceId or TextToSpeechOptions.Voice " +
                "(call GetVoicesAsync to discover voice ids for your account).");

        var request = new TTSRequest(text, voiceId, config.Model)
        {
            Language = config.Language,
            Output = new Output
            {
                AudioFormat = config.AudioFormat,
                // Typecast tempo is a multiplier where 1.0 is normal; leave null at the default to keep Typecast's own default.
                AudioTempo = options.SpeechRate is 1.0f ? null : options.SpeechRate
            }
        };

        if (config.Emotion is { } emotion)
            request.Prompt = new Prompt(emotion, config.EmotionIntensity);

        logger.LogDebug("Synthesizing speech via Typecast using voice {Voice}", voiceId);
        var response = await client.Get(config.ApiKey).TextToSpeechAsync(request, cancellationToken);

        logger.LogDebug("Typecast TTS synthesized {Bytes} bytes ({Format})", response.AudioData.Length, response.Format);
        return response.ToStream();
    }

    public void Dispose() => client.Dispose();
}
