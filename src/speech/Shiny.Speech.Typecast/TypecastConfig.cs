using Typecast.Models;

namespace Shiny.Speech.Typecast;

public record TypecastConfig
{
    /// <summary>
    /// Your Typecast API key (see https://typecast.ai — Account &gt; API Keys).
    /// </summary>
    public required string ApiKey { get; set; }

    /// <summary>
    /// Voice id used when no voice is supplied via <see cref="TextToSpeechOptions.Voice"/>.
    /// Typecast has no fixed public default voice — call
    /// <see cref="Shiny.Speech.Cloud.ITextToSpeechProvider.GetVoicesAsync"/> to discover ids for your account.
    /// Leave unset and pass a voice per-request instead if you prefer.
    /// </summary>
    public string DefaultVoiceId { get; set; } = String.Empty;

    /// <summary>
    /// The Typecast TTS model. Default: <see cref="TTSModel.SsfmV30"/>.
    /// </summary>
    public TTSModel Model { get; set; } = TTSModel.SsfmV30;

    /// <summary>
    /// Optional language hint. When null, Typecast auto-detects the language from the text.
    /// </summary>
    public LanguageCode? Language { get; set; }

    /// <summary>
    /// Optional emotion applied to every utterance. When null, Typecast uses its neutral default.
    /// </summary>
    public EmotionPreset? Emotion { get; set; }

    /// <summary>
    /// Intensity (roughly 0.0–2.0) for <see cref="Emotion"/>. Ignored when <see cref="Emotion"/> is null.
    /// </summary>
    public double? EmotionIntensity { get; set; }

    /// <summary>
    /// Output audio container. Default: <see cref="AudioFormat.Mp3"/> — the format the platform
    /// <c>IAudioPlayer</c> plays back.
    /// </summary>
    public AudioFormat AudioFormat { get; set; } = AudioFormat.Mp3;
}
