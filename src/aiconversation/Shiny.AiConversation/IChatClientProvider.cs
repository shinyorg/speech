using Microsoft.Extensions.AI;

namespace Shiny.AiConversation;

/// <summary>
/// Provides an <see cref="IChatClient"/> instance for AI sessions.
/// Implementations handle authentication, token management, and client construction.
/// </summary>
/// <remarks>
/// The session runs the tool loop itself. A client without function invocation is wrapped with
/// <c>UseFunctionInvocation()</c> automatically; a client that already has it is used as-is.
/// </remarks>
public interface IChatClientProvider
{
    /// <summary>
    /// Retrieves or creates a chat client, performing any required authentication.
    /// </summary>
    /// <param name="cancelToken">Token to cancel the operation.</param>
    /// <returns>A configured <see cref="IChatClient"/> ready for use.</returns>
    Task<IChatClient> GetChatClient(CancellationToken cancelToken = default);

    /// <summary>
    /// The model's context window in tokens, used when <see cref="AiSessionSettings.ContextWindow"/> is not set.
    /// Null means unknown: sessions then report raw token counts but no context pressure.
    /// </summary>
    int? ContextWindow => null;

    /// <summary>
    /// True when the endpoint accepts images inside tool results (used by the camera tool).
    /// </summary>
    bool SupportsImagesInToolResults => false;
}
