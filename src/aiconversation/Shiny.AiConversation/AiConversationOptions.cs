using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AiConversation;
using Shiny.AiConversation.Infrastructure;

namespace Shiny;

/// <summary>
/// Configuration options for the Shiny AI service, used during registration
/// to set up the chat client provider, message store, and optional AI tools.
/// </summary>
public class AiConversationOptions(IServiceCollection services)
{
    public IServiceCollection Services => services;
    
    /// <summary>
    /// Will call for Shiny Speech Service registration if true (default)
    /// </summary>
    public bool AutoAddSpeechServices { get; set; } = true;

    /// <summary>
    /// Registers an <see cref="IContextProvider"/> implementation that populates the AI context with tools and system prompts.
    /// </summary>
    /// <typeparam name="TContextProvider"></typeparam>
    /// <returns></returns>
    public AiConversationOptions AddContextProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TContextProvider>()
        where TContextProvider : class, IContextProvider
    {
        services.AddSingleton<IContextProvider, TContextProvider>();
        return this;
    }
    
    
    /// <summary>
    /// Registers the <see cref="IChatClientProvider"/> implementation used to obtain chat clients.
    /// </summary>
    /// <typeparam name="TTokenProvider">The concrete provider type.</typeparam>
    /// <returns>This options instance for chaining.</returns>
    public AiConversationOptions SetChatClientProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTokenProvider>()
        where TTokenProvider : class, IChatClientProvider
    {
        services.TryAddSingleton<IChatClientProvider, TTokenProvider>();
        return this;
    }

    /// <summary>
    /// Registers an <see cref="IMessageStore"/> implementation for persisting chat history.
    /// Optionally registers a <see cref="ChatLookupAITool"/> that allows the AI to search past conversations.
    /// </summary>
    /// <typeparam name="TMessageStore">The concrete message store type.</typeparam>
    /// <returns>This options instance for chaining.</returns>
    public AiConversationOptions SetMessageStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMessageStore>()
        where TMessageStore : class, IMessageStore
    {
        services.AddSingleton<IMessageStore, TMessageStore>();
        return this;
    }
    

    /// <summary>
    ///
    /// </summary>
    /// <typeparam name="TSoundProvider"></typeparam>
    /// <returns></returns>
    public AiConversationOptions SetSoundProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSoundProvider>()
        where TSoundProvider : class, ISoundProvider
    {
        services.AddSingleton<ISoundProvider, DefaultSoundPlayer>();
        return this;
    }

    /// <summary>
    /// Registers optional AI tools that allow the AI to list available voices, play voice samples,
    /// and change its own text-to-speech voice during a conversation.
    /// Requires speech services (AutoAddSpeechServices must be true, or ITextToSpeechService registered separately).
    /// </summary>
    /// <returns>This options instance for chaining.</returns>
    public AiConversationOptions AddVoiceSelectionTools()
    {
        services.AddSingleton<IContextProvider, VoiceSelectionContextProvider>();
        return this;
    }
}