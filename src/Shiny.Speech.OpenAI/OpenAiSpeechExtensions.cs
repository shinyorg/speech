using Microsoft.Extensions.DependencyInjection;
using Shiny.Speech.OpenAI;

namespace Shiny;

public static class OpenAiSpeechExtensions
{
    public static IServiceCollection AddOpenAiSpeech(
        this IServiceCollection services,
        string apiKey,
        bool speechToText = true,
        bool textToSpeech = true)
    {
        var config = new OpenAiSpeechConfig { ApiKey = apiKey };
        return services.AddOpenAiSpeech(config, speechToText, textToSpeech);
    }

    public static IServiceCollection AddOpenAiSpeech(
        this IServiceCollection services,
        OpenAiSpeechConfig config,
        bool speechToText = true,
        bool textToSpeech = true)
    {
        services.AddSingleton(config);

        if (speechToText)
            services.AddCloudSpeechToText<OpenAiSpeechToTextProvider>();

        if (textToSpeech)
            services.AddCloudTextToSpeech<OpenAiTextToSpeechProvider>();

        return services;
    }
}
