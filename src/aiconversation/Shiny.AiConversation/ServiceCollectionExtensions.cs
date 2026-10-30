using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AiConversation;
using Shiny.AiConversation.Infrastructure;

namespace Shiny;

/// <summary>
/// Extension methods for registering Shiny AI Conversation with dependency injection.
/// </summary>
public static class AiConversationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAiSessionManager"/> and its dependencies. By default an <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// is resolved from DI and sessions are kept in memory.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">Callback to configure the provider, store, interceptors and tools.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddShinyAiConversation(this IServiceCollection services, Action<AiConversationOptions>? configure = null)
    {
        var options = new AiConversationOptions(services);
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IChatClientProvider, InjectedChatClientProvider>();
        services.TryAddSingleton<IAiSessionStore, InMemoryAiSessionStore>();
        services.TryAddSingleton<IAiMemoryStore, InMemoryAiMemoryStore>();
        services.TryAddSingleton<IContextCompactor, ChatClientContextCompactor>();
        services.TryAddSingleton<IAiAttachmentStore, InMemoryAiAttachmentStore>();
        services.TryAddSingleton(options.MemoryOptions);
        services.TryAddSingleton<IAiSessionManager, AiSessionManager>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISessionInterceptor, DefaultSessionInterceptor>());

        return services;
    }
}
