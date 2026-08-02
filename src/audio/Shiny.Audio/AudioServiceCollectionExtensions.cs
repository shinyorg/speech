using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Audio;

namespace Shiny;

public static class AudioServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform audio capture (<see cref="IAudioSource"/>), playback
    /// (<see cref="IAudioPlayer"/>) and recording (<see cref="IAudioRecorder"/>) services.
    /// </summary>
    public static IServiceCollection AddAudioServices(this IServiceCollection services)
    {
        services
            .AddAudioSource()
            .AddAudioPlayer()
            .AddAudioMonitor()
            .AddAudioDevices()
            .AddAudioRecorder();

        // One-stop discovery facade over the focused services above (which remain injectable directly).
        services.TryAddSingleton<IAudio, AudioFacade>();
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

    /// <summary>
    /// Registers WAV recording (<see cref="IAudioRecorder"/>). Platform-agnostic — it builds on
    /// whatever <see cref="IAudioSource"/> is registered, so it works everywhere capture does,
    /// including Linux via <c>AddLinuxAudio()</c>.
    /// </summary>
    /// <remarks>
    /// Transient, matching <see cref="IAudioSource"/>: a recorder owns one capture session, so each
    /// recording gets its own.
    /// </remarks>
    public static IServiceCollection AddAudioRecorder(this IServiceCollection services)
    {
        services.TryAddTransient<IAudioRecorder, AudioRecorder>();
        return services;
    }

    /// <summary>
    /// Registers the live microphone monitor (<see cref="IAudioMonitor"/>) — mic-to-output
    /// passthrough. Only implemented on iOS/Mac Catalyst and Android.
    /// </summary>
    public static IServiceCollection AddAudioMonitor(this IServiceCollection services)
    {
#if APPLE
        services.TryAddSingleton<IAudioMonitor, AppleAudioMonitor>();
#elif ANDROID
        services.TryAddSingleton<IAudioMonitor, AndroidAudioMonitor>();
#endif
        return services;
    }

    /// <summary>
    /// Registers audio input/output route enumeration (<see cref="IAudioDevices"/>). Only
    /// implemented on iOS/Mac Catalyst and Android.
    /// </summary>
    public static IServiceCollection AddAudioDevices(this IServiceCollection services)
    {
#if APPLE
        services.TryAddSingleton<IAudioDevices, AppleAudioDevices>();
#elif ANDROID
        services.TryAddSingleton<IAudioDevices, AndroidAudioDevices>();
#endif
        return services;
    }
}
