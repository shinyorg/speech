using System.Globalization;

namespace Shiny.Speech;

public record SpeechRecognitionOptions
{
    public CultureInfo? Culture { get; init; }
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public bool PreferOnDevice { get; init; }
    public string[]? Keywords { get; init; }
}
