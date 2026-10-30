using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AiConversation;
using Shiny.AiConversation.Voice;
using Shiny.AiConversation.Voice.Infrastructure;

namespace Shiny;

public class AiConversationVoiceOptions(IServiceCollection services)
{
    public IServiceCollection Services => services;

    /// <summary>
    /// Replaces the built-in state-change sounds.
    /// </summary>
    public AiConversationVoiceOptions SetSoundProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSoundProvider>()
        where TSoundProvider : class, ISoundProvider
    {
        services.TryAddSingleton<ISoundProvider, TSoundProvider>();
        return this;
    }

    /// <summary>
    /// Lets the AI list voices, play samples and change its own voice.
    /// </summary>
    public AiConversationVoiceOptions AddVoiceSelectionTools()
    {
        services.AddSingleton<ISessionInterceptor, VoiceSelectionInterceptor>();
        return this;
    }
}


public static class AiConversationVoiceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IVoiceConversation"/>. Speech-to-text and text-to-speech are NOT registered here:
    /// register the Shiny.Speech providers you want (native, Azure, OpenAI, ElevenLabs…) yourself.
    /// </summary>
    public static IServiceCollection AddShinyAiConversationVoice(this IServiceCollection services, Action<AiConversationVoiceOptions>? configure = null)
    {
        configure?.Invoke(new AiConversationVoiceOptions(services));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISoundProvider, DefaultSoundProvider>();
        services.TryAddSingleton<IVoiceConversation, VoiceConversation>();
        services.AddSingleton<IAudioFocusParticipant>(sp => (IAudioFocusParticipant)sp.GetRequiredService<IVoiceConversation>());
        return services;
    }
}
