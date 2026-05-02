using System.Globalization;
using Shiny.Speech;

namespace Shiny.Speech.Cloud;

/// <summary>
/// Implement this interface to plug in a cloud text-to-speech provider (Azure, ElevenLabs, etc.).
/// The provider synthesizes text into an audio stream for playback.
/// </summary>
public interface ITextToSpeechProvider
{
    Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CultureInfo? culture = null, CancellationToken cancellationToken = default);
    Task<Stream> SynthesizeAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default);
}
