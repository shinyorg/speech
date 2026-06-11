using Microsoft.Extensions.DependencyInjection;
using Shiny.Speech;
using Shiny.Speech.Cloud;

namespace Shiny;

public static class SpeechCloudServiceCollectionExtensions
{
    public static IServiceCollection AddCloudSpeechToText<TProvider>(this IServiceCollection services)
        where TProvider : class, ISpeechToTextProvider
    {
        services.AddAudioSource();
        services.AddSingleton<ISpeechToTextProvider, TProvider>();
        services.AddSingleton<ISpeechToTextService, CloudSpeechToText>();
        return services;
    }

    public static IServiceCollection AddCloudSpeechToText(this IServiceCollection services, ISpeechToTextProvider provider)
    {
        services.AddAudioSource();
        services.AddSingleton(provider);
        services.AddSingleton<ISpeechToTextService, CloudSpeechToText>();
        return services;
    }

    public static IServiceCollection AddCloudTextToSpeech<TProvider>(this IServiceCollection services)
        where TProvider : class, ITextToSpeechProvider
    {
        services.AddAudioPlayer();
        services.AddSingleton<ITextToSpeechProvider, TProvider>();
        services.AddSingleton<ITextToSpeechService, CloudTextToSpeech>();
        return services;
    }

    public static IServiceCollection AddCloudTextToSpeech(this IServiceCollection services, ITextToSpeechProvider provider)
    {
        services.AddAudioPlayer();
        services.AddSingleton(provider);
        services.AddSingleton<ITextToSpeechService, CloudTextToSpeech>();
        return services;
    }
}
