using Microsoft.Extensions.DependencyInjection;
using Shiny.AiConversation;
using Shiny.AiConversation.Maui;
using Shiny.Maui.Controls.Chat;

namespace Shiny;

public static class AiConversationMauiRegistrationExtensions
{
    extension(AiConversationOptions options)
    {
        /// <summary>
        /// Registers an <see cref="IChatSessionProvider"/> over the AI conversation service so a plain
        /// <c>ChatView</c> can be bound to it. Not required when using <see cref="AiChatView"/>, which
        /// creates its own provider from its bindable properties.
        /// </summary>
        /// <param name="configure">Optional bot identity / history settings.</param>
        public AiConversationOptions AddChatSessionProvider(Action<AiChatSettings>? configure = null)
        {
            var settings = new AiChatSettings();
            configure?.Invoke(settings);

            options.Services.AddSingleton(settings);
            options.Services.AddSingleton<AiChatSessionProvider>();
            options.Services.AddSingleton<IChatSessionProvider>(sp => sp.GetRequiredService<AiChatSessionProvider>());
            return options;
        }
    }
}
