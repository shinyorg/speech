namespace Shiny.Speech;

public record SpeechRecognitionError(
    string Message,
    Exception? Exception = null
);
