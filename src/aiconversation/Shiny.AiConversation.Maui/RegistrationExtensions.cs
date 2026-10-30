using Microsoft.Extensions.DependencyInjection;
using Shiny.AiConversation;
using Shiny.AiConversation.Maui;

namespace Shiny;

public static class AiConversationMauiRegistrationExtensions
{
    extension(AiConversationOptions options)
    {
        /// <summary>
        /// Registers <see cref="AiChatSettings"/> defaults: every <see cref="AiChatView"/> uses them for the properties
        /// it doesn't set itself.
        /// </summary>
        public AiConversationOptions ConfigureChatView(Action<AiChatSettings> configure)
        {
            var settings = new AiChatSettings();
            configure(settings);
            options.Services.AddSingleton(settings);
            return options;
        }
    }
}
