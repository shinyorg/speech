using Shiny.Speech;

namespace Shiny.Speech.Cloud;

/// <summary>
/// Implement this interface to plug in a cloud speech-to-text provider (Azure, Google, AWS, etc.).
/// The provider receives raw PCM audio data and yields recognition results.
/// </summary>
public interface ISpeechToTextProvider
{
    IAsyncEnumerable<SpeechRecognitionResult> RecognizeAsync(
        Stream audioStream,
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Raised when the provider encounters a non-fatal error during a continuous session
    /// (e.g. a transient network failure between chunked requests). Recognition keeps
    /// running; subscribers receive the error so they can log or surface it without the
    /// underlying <see cref="RecognizeAsync"/> enumerator terminating.
    /// </summary>
    event EventHandler<SpeechRecognitionError>? Error;
}
