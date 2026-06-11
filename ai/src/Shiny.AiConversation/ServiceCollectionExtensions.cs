using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AiConversation;
using Shiny.AiConversation.Infrastructure;

namespace Shiny;

/// <summary>
/// Extension methods for registering the Shiny AI service with dependency injection.
/// </summary>
public static class AiConversationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Shiny AI service and its dependencies. By default, an <see cref="IChatClient"/> is resolved from DI.
    /// Use <see cref="AiConversationOptions.SetChatClientProvider{TTokenProvider}"/> for custom authentication scenarios.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">Callback to configure the AI service options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddShinyAiConversation(this IServiceCollection services, Action<AiConversationOptions> configure)
    {
        if (!services.Any(x => x.ServiceType == typeof(ContextProvider)))
        {
            services.AddSingleton<ContextProvider>();
            services.AddSingleton<IContextProvider>(sp => sp.GetRequiredService<ContextProvider>());
        }

        var options = new AiConversationOptions(services);
        configure.Invoke(options);
        
        if (options.AutoAddSpeechServices)
        {
            services.AddAudioPlayer();
            services.AddSpeechServices();
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IChatClientProvider, InjectedChatClientProvider>();
        services.TryAddSingleton<IAiConversationService, AiConversationService>();
        services.TryAddSingleton<ISoundProvider, DefaultSoundPlayer>();
        
        return services;
    }
}