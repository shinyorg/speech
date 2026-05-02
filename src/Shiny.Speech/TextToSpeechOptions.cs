using System.Globalization;

namespace Shiny.Speech;

public record TextToSpeechOptions
{
    public CultureInfo? Culture { get; init; }
    public VoiceInfo? Voice { get; init; }
    public float SpeechRate { get; init; } = 1.0f;
    public float Pitch { get; init; } = 1.0f;
    public float Volume { get; init; } = 1.0f;
}
