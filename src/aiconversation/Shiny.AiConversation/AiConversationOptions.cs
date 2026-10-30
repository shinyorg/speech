using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AiConversation;
using Shiny.AiConversation.Infrastructure;

namespace Shiny;

/// <summary>
/// Configuration used during registration to set the chat client provider, session store, interceptors and tools.
/// </summary>
public class AiConversationOptions(IServiceCollection services)
{
    public IServiceCollection Services => services;


    /// <summary>
    /// Registers the <see cref="IChatClientProvider"/> used to obtain chat clients. Without one, an
    /// <see cref="IChatClient"/> is resolved from DI.
    /// </summary>
    public AiConversationOptions SetChatClientProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>()
        where TProvider : class, IChatClientProvider
    {
        services.TryAddSingleton<IChatClientProvider, TProvider>();
        return this;
    }


    /// <summary>
    /// Registers the <see cref="IAiSessionStore"/>. Defaults to <see cref="InMemoryAiSessionStore"/>.
    /// </summary>
    public AiConversationOptions SetSessionStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>()
        where TStore : class, IAiSessionStore
    {
        services.TryAddSingleton<IAiSessionStore, TStore>();
        return this;
    }


    /// <summary>
    /// Registers an <see cref="ISessionInterceptor"/> that adds tools and prompts to sessions and reacts to turns.
    /// </summary>
    public AiConversationOptions AddInterceptor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TInterceptor>()
        where TInterceptor : class, ISessionInterceptor
    {
        services.AddSingleton<ISessionInterceptor, TInterceptor>();
        return this;
    }


    /// <summary>
    /// Adds a tool to every session, or only to sessions with one of <paramref name="profiles"/>.
    /// </summary>
    public AiConversationOptions AddTool(AITool tool, AiToolFlags flags = AiToolFlags.None, params string[] profiles)
    {
        services.AddSingleton(new AiToolRegistration(tool, flags, profiles));
        return this;
    }
}
