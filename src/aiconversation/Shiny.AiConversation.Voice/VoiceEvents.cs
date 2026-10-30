namespace Shiny.AiConversation.Voice;

public abstract record VoiceEvent(DateTimeOffset Timestamp);

public record VoiceStateChanged(DateTimeOffset Timestamp, VoiceState State) : VoiceEvent(Timestamp);

/// <summary>
/// A partial transcript while the user is talking. Coalescable: a slow subscriber may miss some.
/// </summary>
public record InterimTranscript(DateTimeOffset Timestamp, string Text) : VoiceEvent(Timestamp);

/// <summary>
/// What was heard and sent to the session.
/// </summary>
public record FinalTranscript(DateTimeOffset Timestamp, string Text) : VoiceEvent(Timestamp);

/// <summary>
/// A sentence is about to be spoken.
/// </summary>
public record SpeakingSentence(DateTimeOffset Timestamp, string Text) : VoiceEvent(Timestamp);

/// <param name="Reason">What stopped the reply.</param>
/// <param name="HeardText">What the user said (the quiet word, answer or new utterance); null for push-to-talk / <see cref="IVoiceConversation.StopSpeaking"/>.</param>
/// <param name="SpokenText">How much of the reply had been spoken, as recorded on the session.</param>
public record SpeechInterrupted(DateTimeOffset Timestamp, InterruptionReason Reason, string? HeardText, string? SpokenText) : VoiceEvent(Timestamp);

public enum InterruptionReason
{
    /// <summary>The whole utterance was a quiet word ("stop").</summary>
    QuietWord,

    /// <summary>The user answered the question being asked ("large", "yeah").</summary>
    Answer,

    /// <summary>New speech in <see cref="InterruptionMode.Full"/>; it becomes the next message.</summary>
    NewSpeech,

    /// <summary><see cref="IVoiceConversation.PushToTalk"/> while speaking.</summary>
    PushToTalk,

    /// <summary><see cref="IVoiceConversation.StopSpeaking"/>.</summary>
    Stop
}

/// <summary>
/// From the end of the user's utterance to the first audio of the reply: the key turn-taking latency.
/// </summary>
public record ResponseLatency(DateTimeOffset Timestamp, TimeSpan Latency) : VoiceEvent(Timestamp);

/// <summary>
/// Microphone or speaker level (0..1). Coalescable.
/// </summary>
public record AudioLevel(DateTimeOffset Timestamp, double Input, double Output) : VoiceEvent(Timestamp);

public record VoiceError(DateTimeOffset Timestamp, Exception Error) : VoiceEvent(Timestamp);
