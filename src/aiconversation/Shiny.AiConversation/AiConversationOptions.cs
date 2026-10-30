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
    /// Memory settings; change with <see cref="ConfigureMemory"/>.
    /// </summary>
    public AiMemoryOptions MemoryOptions { get; private set; } = new();


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
    /// Registers the <see cref="IAiMemoryStore"/>. Defaults to <see cref="InMemoryAiMemoryStore"/>.
    /// </summary>
    public AiConversationOptions SetMemoryStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>()
        where TStore : class, IAiMemoryStore
    {
        services.TryAddSingleton<IAiMemoryStore, TStore>();
        return this;
    }


    /// <summary>
    /// Registers the <see cref="IAiAttachmentStore"/> for images. Defaults to <see cref="InMemoryAiAttachmentStore"/>.
    /// </summary>
    public AiConversationOptions SetAttachmentStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>()
        where TStore : class, IAiAttachmentStore
    {
        services.TryAddSingleton<IAiAttachmentStore, TStore>();
        return this;
    }


    /// <summary>
    /// Stores image attachments as files under <paramref name="directory"/> (e.g. the app data folder).
    /// </summary>
    public AiConversationOptions UseFileAttachments(string directory)
    {
        services.TryAddSingleton<IAiAttachmentStore>(new FileAiAttachmentStore(directory));
        return this;
    }


    /// <summary>
    /// Changes the assistant memory settings (limits, or <c>Enabled = false</c> to turn memory off).
    /// </summary>
    public AiConversationOptions ConfigureMemory(Func<AiMemoryOptions, AiMemoryOptions> configure)
    {
        this.MemoryOptions = configure(this.MemoryOptions);
        return this;
    }


    /// <summary>
    /// Replaces the default compactor (which asks the session's model for a rolling summary).
    /// </summary>
    public AiConversationOptions SetContextCompactor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCompactor>()
        where TCompactor : class, IContextCompactor
    {
        services.TryAddSingleton<IContextCompactor, TCompactor>();
        return this;
    }


    /// <summary>
    /// Gives each session a short title after its first exchange (generated in the background).
    /// </summary>
    public AiConversationOptions AddSessionTitles()
    {
        services.AddSingleton<ISessionInterceptor, SessionTitleInterceptor>();
        return this;
    }


    /// <summary>
    /// Tells sessions what the user's most recent other conversations were about, from their compaction summaries.
    /// </summary>
    /// <param name="sessionCount">How many recent sessions to include.</param>
    /// <param name="profiles">Limit to sessions with one of these profiles; none means every session.</param>
    public AiConversationOptions AddLongTermMemory(int sessionCount = 3, params string[] profiles)
    {
        services.AddSingleton<ISessionInterceptor>(sp => new LongTermMemoryInterceptor(sp.GetRequiredService<IAiSessionStore>(), sessionCount, profiles));
        return this;
    }


    /// <summary>
    /// Lets the assistant search past conversations (and the compacted part of the current one) with the
    /// silent <c>search_conversations</c> and <c>read_conversation</c> tools.
    /// </summary>
    /// <param name="profiles">Limit to sessions with one of these profiles; none means every session.</param>
    public AiConversationOptions AddConversationSearch(params string[] profiles)
    {
        services.AddSingleton<ISessionInterceptor>(sp => new ConversationSearchInterceptor(sp.GetRequiredService<IAiSessionStore>(), profiles));
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
