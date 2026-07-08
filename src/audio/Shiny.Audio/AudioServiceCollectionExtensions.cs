using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Audio;

namespace Shiny;

public static class AudioServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform audio capture (<see cref="IAudioSource"/>) and playback
    /// (<see cref="IAudioPlayer"/>) services.
    /// </summary>
    public static IServiceCollection AddAudioServices(this IServiceCollection services) => services
        .AddAudioSource()
        .AddAudioPlayer();

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
