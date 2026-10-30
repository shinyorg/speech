using Shiny.Speech;

namespace Shiny.AiConversation.Voice;

/// <summary>
/// Talks to an <see cref="IAiSession"/> by voice: wake word, push-to-talk, follow-up answers, interruption and
/// sentence-by-sentence text-to-speech. One session is attached at a time (the microphone is a single
/// resource). The session owns the conversation; this owns the audio, and only uses the session's public API.
/// </summary>
public interface IVoiceConversation : IAsyncDisposable
{
    VoiceState State { get; }

    /// <summary>
    /// The attached session, if any. The caller still owns (and disposes) it.
    /// </summary>
    IAiSession? Session { get; }

    VoiceOptions? Options { get; }

    /// <summary>
    /// The voice used for replies. Starts from <see cref="VoiceOptions.TextToSpeech"/>; the voice-selection
    /// tools change it at runtime.
    /// </summary>
    TextToSpeechOptions? TextToSpeech { get; set; }

    /// <summary>
    /// Hot broadcast of voice events; the first item is the current <see cref="VoiceStateChanged"/>.
    /// </summary>
    IAsyncEnumerable<VoiceEvent> GetEvents(CancellationToken ct = default);

    /// <summary>
    /// Attaches a session. With a <see cref="VoiceOptions.WakeWord"/> the microphone opens right away;
    /// otherwise nothing listens until <see cref="PushToTalk"/>.
    /// </summary>
    Task Attach(IAiSession session, VoiceOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Listens for one utterance now, with or without a wake word. While the AI is speaking it stops the
    /// reply (recording what was heard) and listens instead.
    /// </summary>
    Task PushToTalk(CancellationToken ct = default);

    /// <summary>
    /// Stops the current reply (recording what was heard) without listening.
    /// </summary>
    Task StopSpeaking();

    /// <summary>
    /// Releases the microphone and speaker (e.g. while the camera is open) until <see cref="Resume"/>.
    /// </summary>
    Task Pause();

    Task Resume();

    /// <summary>
    /// Stops listening and speaking and releases the session.
    /// </summary>
    Task Detach();

    /// <summary>
    /// The speech-to-text permission state, passed through as the platform reports it.
    /// </summary>
    Task<AccessState> RequestAccess();
}


public enum VoiceState
{
    /// <summary>
    /// Not attached, or attached without a wake word and waiting for push-to-talk. The microphone is closed.
    /// </summary>
    Off,
    WaitingForWakeWord,
    Listening,

    /// <summary>
    /// The AI asked something; listening for the answer without the wake word, until <see cref="VoiceOptions.FollowUpTimeout"/>.
    /// </summary>
    FollowUp,

    /// <summary>
    /// Waiting for the model's reply.
    /// </summary>
    Thinking,
    Speaking,

    /// <summary>
    /// Microphone and speaker released (see <see cref="IVoiceConversation.Pause"/>).
    /// </summary>
    Paused
}


/// <summary>
/// What happens when the user talks while the AI is speaking.
/// </summary>
public enum InterruptionMode
{
    /// <summary>
    /// The microphone is muted while speaking (plus a short settle window), so nothing interrupts.
    /// Quiet words are ignored in this mode.
    /// </summary>
    Off,

    /// <summary>
    /// Only an utterance that is entirely a quiet word ("stop", "cancel"…) stops the reply. The default.
    /// </summary>
    QuietWords,

    /// <summary>
    /// Any speech stops the reply and becomes the next message; quiet words still just stop. Best with headphones.
    /// </summary>
    Full
}


/// <summary>
/// How replies are delivered.
/// </summary>
public enum AiAcknowledgement
{
    /// <summary>
    /// No sounds and no text-to-speech (replies are text only).
    /// </summary>
    None,

    /// <summary>
    /// Short sounds at state changes, no text-to-speech.
    /// </summary>
    AudioBlip,

    /// <summary>
    /// Text-to-speech, with the model asked to be brief.
    /// </summary>
    LessWordy,

    /// <summary>
    /// Text-to-speech of the full reply.
    /// </summary>
    Full
}
