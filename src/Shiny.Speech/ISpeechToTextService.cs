using System.Runtime.CompilerServices;

namespace Shiny.Speech;

public interface ISpeechToTextService
{
    bool IsSupported { get; }
    Task<AccessState> RequestAccess();

    IAsyncEnumerable<SpeechRecognitionResult> ContinuousRecognize(
        SpeechRecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    );

    Task<string?> ListenUntilSilence(
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default
    );
}
