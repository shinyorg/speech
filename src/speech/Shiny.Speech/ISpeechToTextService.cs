namespace Shiny.Speech;

public interface ISpeechToTextService
{
    bool IsSupported { get; }
    bool IsListening { get; }
    Task<AccessState> RequestAccess();

    Task Start(SpeechRecognitionOptions? options = null);
    Task Stop();

    event EventHandler<SpeechRecognitionResult> ResultReceived;
    event EventHandler<string> KeywordHeard;
    event EventHandler<SpeechRecognitionError> Error;
}
