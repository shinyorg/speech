using Shiny.Maui.Controls.Chat;

namespace Shiny.AiConversation.Maui;

/// <summary>
/// Bridges one <see cref="IAiSession"/> onto the provider-driven <c>ChatView</c>. Every session id the control
/// asks for resolves to that session.
/// </summary>
/// <remarks>
/// <see cref="AiChatView"/> creates one of these for you; use it directly only with a plain <c>ChatView</c>.
/// </remarks>
public class AiChatSessionProvider(IAiSession session, AiChatSettings settings, IAiAttachmentStore? attachments = null) : IChatSessionProvider
{
    public IAiSession Session => session;
    public AiChatSettings Settings => settings;

    /// <summary>
    /// The live chat session; choice buttons route their answers through it.
    /// </summary>
    internal AiChatSession? Current { get; private set; }

    /// <summary>
    /// Raised when the session's context pressure changes (Warning / Critical / Normal).
    /// </summary>
    public event EventHandler<ContextSnapshot>? ContextPressureChanged;

    public Task<IChatSession> CreateSessionAsync(string[] userIds, CancellationToken cancellationToken = default)
        => Task.FromResult<IChatSession>(this.Track());

    public Task<IChatSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IChatSession>(this.Track());

    AiChatSession Track()
    {
        var chat = new AiChatSession(session, settings, attachments);
        chat.ContextPressureChanged += (s, e) => this.ContextPressureChanged?.Invoke(this, e);
        return this.Current = chat;
    }
}
