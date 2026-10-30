namespace Shiny.AiConversation;

/// <summary>
/// One request/response exchange in a session, including any tool calls the model makes.
/// </summary>
public interface IAiTurn
{
    string TurnId { get; }

    /// <summary>
    /// The turn's events in order. Re-enumerating replays them from the start. Ends after
    /// <see cref="TurnCompleted"/>, <see cref="TurnInterrupted"/> or <see cref="TurnFailed"/>.
    /// </summary>
    IAsyncEnumerable<AiTurnEvent> Events { get; }

    /// <summary>
    /// Completes when the turn ends. Never faults: failures are reported as <see cref="AiTurnOutcome.Failed"/>.
    /// </summary>
    Task<AiTurnResult> Completion { get; }

    /// <summary>
    /// Stops the turn because the user interrupted. Pass how far text-to-speech got so the model is told
    /// what was actually heard. Calling it after the turn completed (the reply was still being spoken)
    /// marks the stored reply as interrupted.
    /// </summary>
    void Interrupt(string? spokenText = null);
}

public record AiTurnResult(
    AiTurnOutcome Outcome,
    AiMessageDocument? Reply,
    IReadOnlyList<AiQuestion> Questions,
    UsageSnapshot? Usage,
    Exception? Error = null
);

public enum AiTurnOutcome
{
    Completed,
    AwaitingUser,
    Interrupted,

    /// <summary>
    /// Replaced by a newer <see cref="IAiSession.Send"/>.
    /// </summary>
    Superseded,
    Failed
}
