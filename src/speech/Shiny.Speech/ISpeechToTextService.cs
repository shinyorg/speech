using Shiny.Audio;

namespace Shiny.Speech;

public interface ISpeechToTextService
{
    bool IsSupported { get; }
    bool IsListening { get; }
    Task<AccessState> RequestAccess();

    Task Start(SpeechRecognitionOptions? options = null);
    Task Stop();

    /// <summary>
    /// True when this service can emit <see cref="InputLevelChanged"/> samples while listening.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>Cloud providers</b> (Azure, OpenAI, ElevenLabs, Microsoft.Extensions.AI):
    /// <c>true</c> on every platform — metered from the <see cref="IAudioSource"/> feeding the provider.</description></item>
    /// <item><description><b>Apple</b> (iOS / Mac Catalyst / macOS): <c>true</c> — metered from the recognizer's own input tap.</description></item>
    /// <item><description><b>Android</b>: <c>true</c> — from the platform recognizer's RMS callback.</description></item>
    /// <item><description><b>Windows / Browser</b>: <c>false</c> — the native recognizers own the mic and expose no level.</description></item>
    /// </list>
    /// </remarks>
    bool IsInputAnalysisSupported { get; }

    event EventHandler<SpeechRecognitionResult> ResultReceived;
    event EventHandler<string> KeywordHeard;
    event EventHandler<SpeechRecognitionError> Error;

    /// <summary>
    /// Fires periodically while listening with the microphone level normalized 0.0 – 1.0 — drive a
    /// "listening" VU meter with it, the counterpart to <see cref="ITextToSpeechService.AudioLevelChanged"/>.
    /// Only fires where <see cref="IsInputAnalysisSupported"/> is true.
    /// </summary>
    /// <remarks>Raised off the UI thread — marshal before binding.</remarks>
    event EventHandler<double>? InputLevelChanged;
}
