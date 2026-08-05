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
    /// <summary>
    /// OpenAI takes delivery direction as natural language via <c>instructions</c>, so inline
    /// annotations are stripped and the tone is rendered as a sentence instead. Requires an
    /// instruction-aware model such as <c>gpt-4o-mini-tts</c>; older <c>tts-1</c> models ignore the
    /// field.
    /// </summary>
    public SpeechToneCapabilities ToneCapabilities
        => SpeechToneCapabilities.Emotion | SpeechToneCapabilities.Instructions;

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

        var resolved = SpeechAnnotations.Resolve(text, options, this.ToneCapabilities);

        // Instructions is still marked experimental in the OpenAI SDK, but it is the only way to
        // deliver tone to gpt-4o-mini-tts. Revisit when it graduates.
#pragma warning disable OPENAI001
        speechOptions.Instructions = BuildInstructions(resolved.Tone);
#pragma warning restore OPENAI001

        logger.LogDebug("Generating speech via OpenAI using voice {Voice}", voiceId);

        var result = await client.GenerateSpeechAsync(resolved.Text, voice, speechOptions, cancellationToken);

        logger.LogDebug("OpenAI TTS completed, {Bytes} bytes", result.Value.ToArray().Length);
        return result.Value.ToStream();
    }

    /// <summary>
    /// Renders a tone as the natural-language direction OpenAI expects. The emotion becomes a lead
    /// sentence and any caller-supplied <see cref="SpeechTone.Instructions"/> follows it, so the
    /// specific instruction refines the general one rather than competing with it.
    /// </summary>
    static string? BuildInstructions(SpeechTone? tone)
    {
        if (tone == null)
            return null;

        var phrase = tone.Emotion switch
        {
            SpeechEmotion.Happy => "Speak in a happy, upbeat tone.",
            SpeechEmotion.Excited => "Speak in an excited, energetic tone.",
            SpeechEmotion.Sad => "Speak in a sad, downcast tone.",
            SpeechEmotion.Angry => "Speak in an angry, sharp tone.",
            SpeechEmotion.Fearful => "Speak in a fearful, anxious tone.",
            SpeechEmotion.Calm => "Speak in a calm, measured tone.",
            SpeechEmotion.Whispering => "Speak in a soft whisper.",
            SpeechEmotion.Shouting => "Speak loudly, as if shouting.",
            SpeechEmotion.Friendly => "Speak in a warm, friendly tone.",
            SpeechEmotion.Serious => "Speak in a serious, formal tone.",
            SpeechEmotion.Sarcastic => "Speak in a dry, sarcastic tone.",
            _ => null
        };

        var instructions = String.Join(" ", new[] { phrase, tone.Instructions }
            .Where(x => !String.IsNullOrWhiteSpace(x)));

        return instructions.Length == 0 ? null : instructions;
    }
}
