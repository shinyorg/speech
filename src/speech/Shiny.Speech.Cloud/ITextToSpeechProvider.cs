using System.Globalization;
using Shiny.Speech;

namespace Shiny.Speech.Cloud;

/// <summary>
/// Implement this interface to plug in a cloud text-to-speech provider (Azure, ElevenLabs, etc.).
/// The provider synthesizes text into an audio stream for playback.
/// </summary>
public interface ITextToSpeechProvider
{
    /// <summary>
    /// What this provider can do with a <see cref="SpeechTone"/> and with inline annotations such
    /// as <c>[excited]</c>. Providers pass this to <see cref="SpeechAnnotations.Resolve"/> so
    /// annotated text degrades correctly instead of being spoken aloud. Defaults to
    /// <see cref="SpeechToneCapabilities.None"/> for custom providers that don't opt in.
    /// </summary>
    SpeechToneCapabilities ToneCapabilities => SpeechToneCapabilities.None;

    Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CultureInfo? culture = null, CancellationToken cancellationToken = default);
    Task<Stream> SynthesizeAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default);
}
