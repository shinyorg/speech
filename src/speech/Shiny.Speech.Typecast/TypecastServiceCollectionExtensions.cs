using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Speech.Typecast;

namespace Shiny;

public static class TypecastServiceCollectionExtensions
{
    /// <summary>
    /// Registers Typecast as the cloud text-to-speech provider. Typecast is TTS-only.
    /// </summary>
    public static IServiceCollection AddTypecastSpeech(this IServiceCollection services, string apiKey)
        => services.AddTypecastSpeech(new TypecastConfig { ApiKey = apiKey });

    /// <summary>
    /// Registers Typecast as the cloud text-to-speech provider using a full <see cref="TypecastConfig"/>.
    /// </summary>
    public static IServiceCollection AddTypecastSpeech(this IServiceCollection services, TypecastConfig config)
    {
        services.TryAddSingleton(config);
        services.AddCloudTextToSpeech<TypecastTextToSpeechProvider>();
        return services;
    }

    /// <summary>Alias for <see cref="AddTypecastSpeech(IServiceCollection, string)"/>.</summary>
    public static IServiceCollection AddTypecastTextToSpeech(this IServiceCollection services, string apiKey)
        => services.AddTypecastSpeech(apiKey);

    /// <summary>Alias for <see cref="AddTypecastSpeech(IServiceCollection, TypecastConfig)"/>.</summary>
    public static IServiceCollection AddTypecastTextToSpeech(this IServiceCollection services, TypecastConfig config)
        => services.AddTypecastSpeech(config);
}
