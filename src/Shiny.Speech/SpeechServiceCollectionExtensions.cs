using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Speech;

namespace Shiny;

public static class SpeechServiceCollectionExtensions
{
    public static IServiceCollection AddSpeechServices(this IServiceCollection services) => services
        .AddSpeechToText()
        .AddTextToSpeech()
        .AddAudioSource()
        .AddAudioPlayer();

    public static IServiceCollection AddSpeechToText(this IServiceCollection services)
    {
#if ANDROID
        services.TryAddSingleton<ActivityProvider>();
        services.TryAddSingleton<ISpeechToTextService, SpeechToTextImpl>();
#elif PLATFORM
        services.TryAddSingleton<ISpeechToTextService, SpeechToTextImpl>();
#else
        if (OperatingSystem.IsBrowser())
            services.TryAddSingleton<ISpeechToTextService, BrowserSpeechToTextService>();
#endif
        return services;
    }

    public static IServiceCollection AddTextToSpeech(this IServiceCollection services)
    {
#if PLATFORM
        services.TryAddSingleton<ITextToSpeechService, TextToSpeechImpl>();
#else
        if (OperatingSystem.IsBrowser())
            services.TryAddSingleton<ITextToSpeechService, BrowserTextToSpeechService>();
#endif
        return services;
    }

    public static IServiceCollection AddAudioSource(this IServiceCollection services)
    {
#if APPLE
        services.TryAddTransient<IAudioSource, AppleAudioSource>();
#elif ANDROID
        services.TryAddTransient<IAudioSource, AndroidAudioSource>();
#elif WINDOWS
        services.TryAddTransient<IAudioSource, WindowsAudioSource>();
#else
        if (OperatingSystem.IsBrowser())
            services.TryAddTransient<IAudioSource, BrowserAudioSource>();
#endif
        return services;
    }

    public static IServiceCollection AddAudioPlayer(this IServiceCollection services)
    {
#if APPLE
        services.TryAddSingleton<IAudioPlayer, AppleAudioPlayer>();
#elif ANDROID
        services.TryAddSingleton<IAudioPlayer, AndroidAudioPlayer>();
#elif WINDOWS
        services.TryAddSingleton<IAudioPlayer, WindowsAudioPlayer>();
#else
        if (OperatingSystem.IsBrowser())
            services.TryAddSingleton<IAudioPlayer, BrowserAudioPlayer>();
#endif
        return services;
    }
}
