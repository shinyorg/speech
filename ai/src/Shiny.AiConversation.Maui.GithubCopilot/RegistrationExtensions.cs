using Microsoft.Extensions.DependencyInjection;
using Shiny.AiConversation;
using Shiny.AiConversation.Maui.GithubCopilot;

namespace Shiny;

public static class GithubCopilotRegistrationExtensions
{
    extension(AiConversationOptions options)
    {
        /// <summary>
        /// Registers the GitHub Copilot chat client using the full device code OAuth flow.
        /// Shows a popup with the device code, opens the browser, and polls until authorized.
        /// Tokens are stored in SecureStorage and refreshed automatically.
        /// </summary>
        public AiConversationOptions AddGithubCopilotChatClient()
        {
            options.Services.AddSingleton<GitHubCopilotChatClientProvider>();
            options.Services.AddSingleton<IChatClientProvider>(sp =>
                sp.GetRequiredService<GitHubCopilotChatClientProvider>());
            return options;
        }
    }
}
