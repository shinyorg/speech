using Microsoft.Extensions.AI;

namespace Shiny.AiConversation;

/// <summary>
/// Adds tools and system prompts to sessions and reacts to turns. Register with
/// <see cref="AiConversationOptions.AddInterceptor{TInterceptor}"/>. Tools are never persisted, so
/// interceptors run on every load (new or rehydrated) and rebuild them.
/// </summary>
public interface ISessionInterceptor
{
    /// <summary>
    /// Runs on <see cref="IAiSessionManager.Start"/> and on every <see cref="IAiSessionManager.Get"/>.
    /// Tools and prompts added here apply to every turn of this instance.
    /// </summary>
    Task OnSessionLoaded(SessionLoadedContext context) => Task.CompletedTask;

    /// <summary>
    /// Runs before each turn is sent. Tools and prompts added here apply to this turn only (RAG, time, location).
    /// </summary>
    Task OnBeforeTurn(TurnContext context) => Task.CompletedTask;

    /// <summary>
    /// Runs after each turn ends, whatever the outcome. Exceptions are logged, never surfaced.
    /// </summary>
    Task OnAfterTurn(TurnContext context, AiTurnResult result) => Task.CompletedTask;
}

[Flags]
public enum AiToolFlags
{
    None = 0,

    /// <summary>
    /// The tool never triggers a spoken "one moment" acknowledgement.
    /// </summary>
    Silent = 1
}

public abstract class SessionContextBase(IAiSession session)
{
    internal List<(AITool Tool, AiToolFlags Flags)> Tools { get; } = [];

    public IAiSession Session => session;
    public IList<string> SystemPrompts { get; } = new List<string>();

    public void AddTool(AITool tool, AiToolFlags flags = AiToolFlags.None)
        => this.Tools.Add((tool, flags));
}

public sealed class SessionLoadedContext(IAiSession session, bool isNew) : SessionContextBase(session)
{
    /// <summary>
    /// True when the session was just created by <see cref="IAiSessionManager.Start"/>.
    /// </summary>
    public bool IsNew => isNew;
}

public sealed class TurnContext(IAiSession session, string turnId, AiInput input) : SessionContextBase(session)
{
    public string TurnId => turnId;
    public AiInput Input => input;
}
