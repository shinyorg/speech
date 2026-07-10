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
    /// True when <see cref="SynthesizeToStreamAsync"/> is supported — i.e. this service can render
    /// audio without playing it aloud. False for the on-device platform synthesizers (which play
    /// live); true for cloud providers that fetch audio bytes.
    /// </summary>
    bool CanSynthesizeToStream { get; }

    /// <summary>
    /// Synthesizes <paramref name="text"/> to an audio stream instead of speaking it aloud, so the
    /// caller can persist or post-process the audio. The container/codec depends on the underlying
    /// provider (e.g. MP3 for most cloud providers). Throws <see cref="NotSupportedException"/> when
    /// <see cref="CanSynthesizeToStream"/> is false.
    /// </summary>
    Task<Stream> SynthesizeToStreamAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default);

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
