using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AiConversation;
using Shiny.AiConversation.GithubCopilot;

namespace Shiny;

public static class GithubCopilotStaticRegistrationExtensions
{
    /// <summary>
    /// Registers the GitHub Copilot chat client using a pre-existing Personal Access Token (PAT).
    /// The PAT is exchanged for a short-lived Copilot token automatically and refreshed as needed.
    /// </summary>
    /// <param name="personalAccessToken">A GitHub PAT with Copilot access.</param>
    /// <param name="modelName">The model to use (default: gpt-4o).</param>
    public static AiConversationOptions AddStaticGithubCopilotChatClient(
        this AiConversationOptions options,
        string personalAccessToken,
        string modelName = "gpt-4o"
    )
    {
        options.Services.TryAddSingleton<IChatClientProvider>(sp =>
            new GithubCopilotStaticChatProvider(sp, personalAccessToken, modelName));
        return options;
    }
}
