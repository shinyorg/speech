using System.Globalization;

namespace Shiny.Speech;

public interface ITextToSpeechService
{
    bool IsSupported { get; }
    Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CultureInfo? culture = null, CancellationToken cancellationToken = default);
    Task SpeakAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default);
    Task StopAsync();
    bool IsSpeaking { get; }
}
