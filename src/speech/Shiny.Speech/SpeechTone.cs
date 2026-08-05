namespace Shiny.Speech;

/// <summary>
/// Portable emotional direction for an utterance. Set it on <see cref="TextToSpeechOptions.Tone"/>
/// and each provider projects it into its own mechanism; providers with no expressive control
/// simply ignore it.
/// </summary>
public record SpeechTone
{
    /// <summary>
    /// The emotional delivery. <see cref="SpeechEmotion.Neutral"/> means "no direction" and is
    /// treated the same as leaving <see cref="TextToSpeechOptions.Tone"/> null.
    /// </summary>
    public SpeechEmotion Emotion { get; init; } = SpeechEmotion.Neutral;

    /// <summary>
    /// Strength of <see cref="Emotion"/> from 0.0 to 2.0, where 1.0 is the provider default.
    /// Maps to Typecast <c>emotion_intensity</c> and Azure <c>styledegree</c>. Providers with no
    /// intensity concept (ElevenLabs, OpenAI, on-device) ignore it.
    /// </summary>
    public float Intensity { get; init; } = 1.0f;

    /// <summary>
    /// Free-text delivery direction ("sound like you're announcing good news to a friend").
    /// Passed verbatim to providers that accept natural language — currently OpenAI's
    /// <c>instructions</c> field, which requires a model such as <c>gpt-4o-mini-tts</c>.
    /// Ignored by every other provider.
    /// </summary>
    public string? Instructions { get; init; }
}
