using Microsoft.Extensions.AI;
using Shiny.Speech;

namespace Shiny.AiConversation;

/// <summary>
/// A centralized AI service that manages chat interactions, speech recognition,
/// wake word detection, and text-to-speech responses.
/// </summary>
public interface IAiConversationService
{
    /// <summary>
    /// Raised when <see cref="Status"/> transitions between Idle / Listening / Thinking / Responding.
    /// </summary>
    event Action<AiState> StatusChanged;

    /// <summary>
    /// Raised when the AI produces a response. The string parameter contains the full response text.
    /// </summary>
    event Action<AiResponse>? AiResponded;

    /// <summary>
    /// Raised for every speech recognition result observed during the current session — useful for live
    /// transcription previews, voice level meters, or other UI feedback. Includes both interim and final results.
    /// </summary>
    event Action<SpeechRecognitionResult>? SpeechResultReceived;

    /// <summary>
    /// Raised for both sides of the conversation: a final user utterance captured by speech-to-text
    /// (<see cref="ConversationSpeechSource.Heard"/>) and the AI response text right before it is spoken
    /// aloud by text-to-speech (<see cref="ConversationSpeechSource.Spoken"/>).
    /// </summary>
    event Action<ConversationSpeech>? SpeechOccurred;

    /// <summary>
    /// Raised when an unrecoverable error stops the service from acting (the active ListenAndTalk or
    /// wake-word loop is aborted, or the underlying speech driver reports a fatal error). Normal
    /// cancellation does not raise this event.
    /// </summary>
    event Action<Exception>? ErrorOccurred;

    /// <summary>
    /// The currently active wake word, or null if wake word detection is not running.
    /// </summary>
    string? WakeWord { get; }

    /// <summary>
    /// Opens a single long-running speech recognition session and listens for the specified wake word.
    /// On each detection, captures the next utterance and forwards it to <see cref="TalkTo"/>. The microphone
    /// stays open across turns (the new Speech 2.0 keep-alive model) until <see cref="StopWakeWord"/> is called.
    /// </summary>
    /// <param name="wakeWord">The keyword phrase to listen for (e.g. "Hey Assistant").</param>
    /// <exception cref="InvalidOperationException">Thrown if any conversation session is already active.</exception>
    Task StartWakeWord(string wakeWord);

    /// <summary>
    /// Stops the active wake word detection loop and returns the service to an idle state.
    /// </summary>
    Task StopWakeWord();

    /// <summary>
    /// When true, the service listens for speech during text-to-speech playback to allow
    /// the user to interrupt or redirect the AI. When false, TTS plays without listening.
    /// </summary>
    bool InterruptionEnabled { get; set; }

    /// <summary>
    /// The current processing state of the service.
    /// </summary>
    AiState Status { get; }

    /// <summary>
    /// Controls how the AI acknowledges responses. Determines whether sounds are played,
    /// text-to-speech is used, or responses are kept concise.
    /// </summary>
    AiAcknowledgement Acknowledgement { get; set; }

    /// <summary>
    /// Options passed to text-to-speech when speaking AI responses aloud.
    /// Setting this directly overrides any value supplied by context providers.
    /// </summary>
    Shiny.Speech.TextToSpeechOptions? TextToSpeechOptions { get; set; }

    /// <summary>
    /// The in-memory chat messages for the current conversation session.
    /// </summary>
    IReadOnlyList<ChatMessage> CurrentChatMessages { get; }

    /// <summary>
    /// Clears all in-memory chat messages for the current conversation session.
    /// Does not affect persisted history.
    /// </summary>
    void ClearCurrentChat();

    /// <summary>
    /// Activates speech-to-text to collect a single utterance and sends it to the AI.
    /// This is intended for manual "push to talk" scenarios when wake word is not in use.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the listening and/or AI processing.</param>
    /// <exception cref="InvalidOperationException">Thrown if wake word detection is currently active.</exception>
    Task ListenAndTalk(CancellationToken cancellationToken);

    /// <summary>
    /// Sends a text message directly to the AI for processing.
    /// </summary>
    /// <param name="message">The user message to send.</param>
    /// <param name="cancellationToken">Token to cancel the AI processing.</param>
    Task TalkTo(string message, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves persisted chat history from the document store with optional filtering.
    /// </summary>
    /// <param name="messageContains">Optional text filter to match against message content.</param>
    /// <param name="startDate">Optional inclusive start date to filter messages.</param>
    /// <param name="endDate">Optional inclusive end date to filter messages.</param>
    /// <param name="limit">Optional maximum number of messages to return.</param>
    /// <returns>A list of chat messages ordered by timestamp.</returns>
    Task<IReadOnlyList<AiChatMessage>> GetChatHistory(
        string? messageContains = null,
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        int? limit = null
    );
    
    
    /// <summary>
    ///
    /// </summary>
    /// <param name="beforeDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ClearChatHistory(DateTimeOffset? beforeDate = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests access to the underlying speech-to-text service. Since all other AI services
    /// are available by default, this effectively checks whether the conversation service can operate.
    /// Returns <see cref="AccessState.Available"/> if speech is ready, or <see cref="AccessState.Restricted"/> otherwise.
    /// </summary>
    /// <returns>
    /// <see cref="AccessState.Available"/> if speech-to-text access is granted;
    /// <see cref="AccessState.Restricted"/> for any other speech access state.
    /// </returns>
    Task<AccessState> RequestAccess();
}

/// <summary>
/// Represents the current processing state of the AI service.
/// </summary>
public enum AiState
{
    /// <summary>The service is idle and ready for input.</summary>
    Idle,
    /// <summary>The service is actively listening for speech input.</summary>
    Listening,
    /// <summary>The service is waiting for the AI to process a request.</summary>
    Thinking,
    /// <summary>The AI is streaming its response.</summary>
    Responding
}

/// <summary>
/// Controls how the AI service acknowledges and delivers responses.
/// </summary>
public enum AiAcknowledgement
{
    /// <summary>No audio feedback or text-to-speech.</summary>
    None,
    /// <summary>Short audio cues are played at state transitions.</summary>
    AudioBlip,
    /// <summary>Text-to-speech is used with a concise system prompt.</summary>
    LessWordy,
    /// <summary>Text-to-speech is used with full, unmodified responses.</summary>
    Full
}

public record AiResponse(
    ChatResponse Response,
    bool WasReadAloud,
    bool ExpectsResponse
);

/// <summary>
/// Identifies which side of the conversation a <see cref="ConversationSpeech"/> event represents.
/// </summary>
public enum ConversationSpeechSource
{
    /// <summary>The user's final utterance as recognized by speech-to-text.</summary>
    Heard,
    /// <summary>The AI's response text about to be spoken aloud by text-to-speech.</summary>
    Spoken
}

/// <summary>
/// A unified payload for conversational speech — either heard from the user (STT) or spoken by the AI (TTS).
/// </summary>
public record ConversationSpeech(ConversationSpeechSource Source, string Text);