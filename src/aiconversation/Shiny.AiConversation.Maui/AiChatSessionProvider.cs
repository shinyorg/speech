using Shiny.Maui.Controls.Chat;

namespace Shiny.AiConversation.Maui;

/// <summary>
/// Bridges <see cref="IAiConversationService"/> onto the provider driven <c>ChatView</c>. There is a
/// single, global AI conversation (backed by the registered <see cref="IMessageStore"/>), so every
/// session id resolves to that same logical conversation.
/// </summary>
/// <remarks>
/// <see cref="AiChatView"/> creates one of these for you. Register it (see
/// <c>AddChatSessionProvider</c>) only when you want to drive a plain <c>ChatView</c> yourself.
/// </remarks>
public class AiChatSessionProvider(IAiConversationService aiService, AiChatSettings settings) : IChatSessionProvider
{
    /// <summary>The bot identity / history settings applied to every session this provider hands out.</summary>
    public AiChatSettings Settings => settings;

    public Task<IChatSession> CreateSessionAsync(string[] userIds, CancellationToken cancellationToken = default)
        => Task.FromResult<IChatSession>(new AiChatSession(aiService, settings));

    public Task<IChatSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IChatSession>(new AiChatSession(aiService, settings));
}
