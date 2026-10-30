using Shiny.Speech;

namespace Shiny.AiConversation.Voice;

public record VoiceOptions
{
    public static IReadOnlyList<string> DefaultQuietWords { get; } =
        ["cancel", "quiet", "shut up", "stop", "nevermind", "never mind", "hush"];

    /// <summary>
    /// Hands-free activation phrase. Null (the default) means push-to-talk only: an always-open microphone
    /// has battery, privacy and (with cloud speech-to-text) cost implications, so only the app can turn it on.
    /// </summary>
    public string? WakeWord { get; init; }

    public InterruptionMode Interruption { get; init; } = InterruptionMode.QuietWords;

    /// <summary>
    /// Phrases that stop a reply when they are the whole utterance. Ignored with <see cref="InterruptionMode.Off"/>.
    /// </summary>
    public IReadOnlyList<string> QuietWords { get; init; } = DefaultQuietWords;

    /// <summary>
    /// While the AI is speaking a question that has choices (including Yes / No), saying one of them stops the
    /// reply and answers it ("large", "the second one", "yeah"). Only a final result that is entirely the answer
    /// counts, so the AI reading its own options aloud doesn't trigger it. Needs QuietWords or Full interruption.
    /// </summary>
    public bool AnswerInterruptions { get; init; } = true;

    /// <summary>
    /// <see cref="InterruptionMode.Full"/> only: a final result below this confidence while the AI is speaking is
    /// treated as echo. A confidence of 0 means "not reported" and is let through.
    /// </summary>
    public float InterruptionMinConfidence { get; init; } = 0.5f;

    /// <summary>
    /// How long to wait for an answer when the AI asked a question, before going back to the wake word (or off).
    /// Null waits forever. A user-initiated turn (wake word / push-to-talk) always waits.
    /// </summary>
    public TimeSpan? FollowUpTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// <see cref="InterruptionMode.Off"/> only: how long recognition stays muted after speaking, so the
    /// recognizer's transcription of the AI's own voice isn't taken as the next utterance.
    /// </summary>
    public TimeSpan EchoSettleWindow { get; init; } = TimeSpan.FromMilliseconds(750);

    public AiAcknowledgement Acknowledgement { get; init; } = AiAcknowledgement.Full;

    /// <summary>
    /// Spoken when the model calls a (non-silent) tool before saying anything, so silence isn't mistaken for a hang.
    /// Null disables it.
    /// </summary>
    public string? ToolAcknowledgement { get; init; } = "One moment.";

    /// <summary>
    /// The instruction added to every voice turn in <see cref="AiAcknowledgement.LessWordy"/>.
    /// </summary>
    public string LessWordyInstruction { get; init; } = "Be concise and brief in your responses. Avoid unnecessary elaboration.";

    /// <summary>
    /// Speech-to-text options. With <see cref="InterruptionMode.QuietWords"/> or <see cref="InterruptionMode.Full"/>
    /// echo cancellation is turned on automatically.
    /// </summary>
    public SpeechRecognitionOptions? SpeechToText { get; init; }

    public TextToSpeechOptions? TextToSpeech { get; init; }
}
