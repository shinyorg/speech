using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Speech.ElevenLabs;

namespace Shiny;

public static class ElevenLabsServiceCollectionExtensions
{
    public static IServiceCollection AddElevenLabsSpeech(
        this IServiceCollection services,
        string apiKey,
        bool speechToText = true,
        bool textToSpeech = true)
        => services.AddElevenLabsSpeech(new ElevenLabsConfig { ApiKey = apiKey }, speechToText, textToSpeech);

    public static IServiceCollection AddElevenLabsSpeech(
        this IServiceCollection services,
        ElevenLabsConfig config,
        bool speechToText = true,
        bool textToSpeech = true)
    {
        services.TryAddSingleton(config);

        if (speechToText)
            services.AddCloudSpeechToText<ElevenLabsSpeechToTextProvider>();

        if (textToSpeech)
            services.AddCloudTextToSpeech<ElevenLabsTextToSpeechProvider>();

        return services;
    }

    public static IServiceCollection AddElevenLabsSpeechToText(this IServiceCollection services, string apiKey)
        => services.AddElevenLabsSpeech(apiKey, speechToText: true, textToSpeech: false);

    public static IServiceCollection AddElevenLabsSpeechToText(this IServiceCollection services, ElevenLabsConfig config)
        => services.AddElevenLabsSpeech(config, speechToText: true, textToSpeech: false);

    public static IServiceCollection AddElevenLabsTextToSpeech(this IServiceCollection services, string apiKey)
        => services.AddElevenLabsSpeech(apiKey, speechToText: false, textToSpeech: true);

    public static IServiceCollection AddElevenLabsTextToSpeech(this IServiceCollection services, ElevenLabsConfig config)
        => services.AddElevenLabsSpeech(config, speechToText: false, textToSpeech: true);
}
