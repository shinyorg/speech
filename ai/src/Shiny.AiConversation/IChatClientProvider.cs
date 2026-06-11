using Microsoft.Extensions.AI;

namespace Shiny.AiConversation;

/// <summary>
/// Provides an <see cref="IChatClient"/> instance for the AI service.
/// Implementations handle authentication, token management, and client construction.
/// </summary>
public interface IChatClientProvider
{
    /// <summary>
    /// Retrieves or creates a chat client, performing any required authentication.
    /// </summary>
    /// <param name="cancelToken">Token to cancel the operation.</param>
    /// <returns>A configured <see cref="IChatClient"/> ready for use.</returns>
    Task<IChatClient> GetChatClient(CancellationToken cancelToken = default);
}