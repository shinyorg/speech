using System.Globalization;

namespace Shiny.Speech;

public record TextToSpeechOptions
{
    public CultureInfo? Culture { get; init; }
    public VoiceInfo? Voice { get; init; }
    public float SpeechRate { get; init; } = 1.0f;
    public float Pitch { get; init; } = 1.0f;
    public float Volume { get; init; } = 1.0f;

    /// <summary>
    /// Portable emotional direction for this utterance. Each provider projects it into whatever
    /// it supports — ElevenLabs inline audio tags, Typecast emotion presets, Azure
    /// <c>mstts:express-as</c>, OpenAI instructions — and providers with no expressive control
    /// ignore it. Takes precedence over any annotation found in the text itself.
    /// </summary>
    public SpeechTone? Tone { get; init; }

    /// <summary>
    /// What to do with bracketed annotations (<c>[excited]</c>) in the text. Defaults to
    /// <see cref="SpeechAnnotationHandling.Auto"/>, which promotes them to a <see cref="Tone"/> and
    /// strips them for providers that would otherwise speak them aloud.
    /// </summary>
    public SpeechAnnotationHandling AnnotationHandling { get; init; } = SpeechAnnotationHandling.Auto;
}
