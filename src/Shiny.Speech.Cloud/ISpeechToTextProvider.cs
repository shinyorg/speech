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
}
