using System.Globalization;

namespace Shiny.Speech;

public interface ITextToSpeechService
{
    bool IsSupported { get; }
    Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CultureInfo? culture = null, CancellationToken cancellationToken = default);
    Task SpeakAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default);
    Task StopAsync();
    bool IsSpeaking { get; }

    /// <summary>
    /// True when this service can emit <see cref="AudioLevelChanged"/> samples while speaking.
    /// </summary>
    bool IsPlayerAnalysisSupported { get; }

    /// <summary>
    /// Fires periodically while speaking with the current output level normalized to 0.0 - 1.0.
    /// Suitable for driving a VU meter UI. Only fires on platforms where
    /// <see cref="IsPlayerAnalysisSupported"/> is true.
    /// </summary>
    event EventHandler<double>? AudioLevelChanged;
}
