using System.Globalization;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging;
using Shiny.Speech.Cloud;

namespace Shiny.Speech.Azure;

public class AzureTextToSpeechProvider(
    AzureSpeechConfig config,
    ILogger<AzureTextToSpeechProvider> logger
) : ITextToSpeechProvider
{
    /// <summary>
    /// Azure expresses emotion through SSML <c>mstts:express-as</c>, so inline annotations are
    /// stripped from the text and promoted into <c>style</c> / <c>styledegree</c>.
    /// </summary>
    public SpeechToneCapabilities ToneCapabilities
        => SpeechToneCapabilities.Emotion | SpeechToneCapabilities.Intensity;

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CultureInfo? culture = null, CancellationToken cancellationToken = default)
    {
        var speechConfig = SpeechConfig.FromSubscription(config.SubscriptionKey, config.Region);
        using var synthesizer = new SpeechSynthesizer(speechConfig, null);

        var voicesResult = await synthesizer.GetVoicesAsync(culture?.Name ?? "");
        if (voicesResult.Reason == ResultReason.VoicesListRetrieved)
        {
            return voicesResult.Voices
                .Select(v => new VoiceInfo(v.ShortName, v.LocalName, new CultureInfo(v.Locale)))
                .ToList();
        }

        logger.LogWarning("Failed to retrieve Azure voices: {Reason}", voicesResult.Reason);
        return [];
    }

    public async Task<Stream> SynthesizeAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new TextToSpeechOptions();

        var speechConfig = SpeechConfig.FromSubscription(config.SubscriptionKey, config.Region);
        speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio16Khz32KBitRateMonoMp3);

        if (options.Voice != null)
            speechConfig.SpeechSynthesisVoiceName = options.Voice.Id;
        else if (options.Culture != null)
            speechConfig.SpeechSynthesisLanguage = options.Culture.Name;

        using var synthesizer = new SpeechSynthesizer(speechConfig, null);

        var voiceName = options.Voice?.Id
            ?? speechConfig.SpeechSynthesisVoiceName
            ?? "en-US-AriaNeural";

        var ratePercent = ((options.SpeechRate - 1.0f) * 100).ToString("+0;-0;+0");
        var pitchPercent = ((options.Pitch - 1.0f) * 100).ToString("+0;-0;+0");
        var volumeValue = (int)(options.Volume * 100);

        var resolved = SpeechAnnotations.Resolve(text, options, this.ToneCapabilities);
        var body = System.Security.SecurityElement.Escape(resolved.Text);

        // Only wrap when a style actually maps — an unstyled utterance keeps the plain SSML shape.
        if (AzureStyleMap.From(resolved.Tone?.Emotion) is { } style)
        {
            // styledegree is 0.01–2, where 1 is the voice's normal expressiveness for that style.
            var degree = Math.Clamp(resolved.Tone!.Intensity, 0.01f, 2f);
            body = $"""<mstts:express-as style="{style}" styledegree="{degree.ToString("0.##", CultureInfo.InvariantCulture)}">{body}</mstts:express-as>""";
        }

        var ssml = $"""
            <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xmlns:mstts="https://www.w3.org/2001/mstts" xml:lang="{options.Culture?.Name ?? "en-US"}">
                <voice name="{voiceName}">
                    <prosody rate="{ratePercent}%" pitch="{pitchPercent}%" volume="{volumeValue}">
                        {body}
                    </prosody>
                </voice>
            </speak>
            """;

        var result = await synthesizer.SpeakSsmlAsync(ssml);

        if (result.Reason == ResultReason.Canceled)
        {
            var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
            logger.LogError("Azure TTS canceled: {Reason} {ErrorDetails}", cancellation.Reason, cancellation.ErrorDetails);
            throw new InvalidOperationException($"Azure TTS failed: {cancellation.ErrorDetails}");
        }

        logger.LogDebug("Azure TTS synthesis completed, {Bytes} bytes", result.AudioData.Length);
        return new MemoryStream(result.AudioData);
    }
}
