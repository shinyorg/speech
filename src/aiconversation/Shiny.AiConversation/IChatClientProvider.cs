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

    /// <summary>
    /// How this provider's endpoint can be asked for a structured <see cref="AiTurn"/>. Defaults to
    /// <see cref="AiStructuredOutputMode.JsonSchema"/> - override it when the endpoint or model rejects
    /// schema-constrained responses. The conversation service falls back to plain text whenever a
    /// structured reply fails to parse, regardless of the mode, so a wrong guess here degrades rather
    /// than breaks.
    /// </summary>
    AiStructuredOutputMode StructuredOutputMode => AiStructuredOutputMode.JsonSchema;
}