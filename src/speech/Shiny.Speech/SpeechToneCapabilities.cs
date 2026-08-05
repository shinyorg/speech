namespace Shiny.Speech;

/// <summary>
/// What a text-to-speech implementation can actually do with a <see cref="SpeechTone"/>.
/// <see cref="SpeechAnnotations.Resolve"/> uses this to decide whether inline annotations such as
/// <c>[excited]</c> survive in the text or get stripped before synthesis.
/// </summary>
[Flags]
public enum SpeechToneCapabilities
{
    /// <summary>
    /// No expressive control. Annotations are stripped from the text and the tone is discarded —
    /// all on-device synthesizers, and ElevenLabs on any model older than v3.
    /// </summary>
    None = 0,

    /// <summary>
    /// Bracketed audio tags in the text are interpreted as performance direction rather than
    /// spoken aloud (ElevenLabs <c>eleven_v3</c>). Tags are left in place.
    /// </summary>
    InlineAnnotations = 1,

    /// <summary>
    /// <see cref="SpeechTone.Emotion"/> maps to a native field (Typecast <c>emotion_preset</c>,
    /// Azure <c>mstts:express-as style</c>).
    /// </summary>
    Emotion = 2,

    /// <summary>
    /// <see cref="SpeechTone.Intensity"/> maps to a native field (Typecast
    /// <c>emotion_intensity</c>, Azure <c>styledegree</c>). Only meaningful alongside
    /// <see cref="Emotion"/>.
    /// </summary>
    Intensity = 4,

    /// <summary>
    /// <see cref="SpeechTone.Instructions"/> is delivered as natural-language direction
    /// (OpenAI <c>instructions</c>).
    /// </summary>
    Instructions = 8
}
