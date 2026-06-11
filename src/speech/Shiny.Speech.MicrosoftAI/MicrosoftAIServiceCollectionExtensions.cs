using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Speech.MicrosoftAI;

namespace Shiny;

public static class MicrosoftAIServiceCollectionExtensions
{
    /// <summary>
    /// Registers ISpeechToTextClient backed by the currently registered ISpeechToTextProvider and IAudioSource.
    /// Requires a cloud provider (Azure, etc.) and platform audio source to be registered first.
    /// </summary>
    public static IServiceCollection AddShinySpeechToTextClient(this IServiceCollection services)
    {
        services.AddAudioSource();
        services.TryAddSingleton<ISpeechToTextClient, ShinySpeechToTextClient>();
        return services;
    }

    /// <summary>
    /// Registers ITextToSpeechClient backed by the currently registered ITextToSpeechProvider.
    /// Requires a cloud provider (Azure, ElevenLabs, etc.) to be registered first.
    /// </summary>
    public static IServiceCollection AddShinyTextToSpeechClient(this IServiceCollection services)
    {
        services.TryAddSingleton<ITextToSpeechClient, ShinyTextToSpeechClient>();
        return services;
    }

    /// <summary>
    /// Registers both ISpeechToTextClient and ITextToSpeechClient backed by Shiny providers.
    /// </summary>
    public static IServiceCollection AddShinySpeechClients(this IServiceCollection services)
    {
        services.AddShinySpeechToTextClient();
        services.AddShinyTextToSpeechClient();
        return services;
    }
}
