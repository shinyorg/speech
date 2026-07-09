using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Audio;

/// <summary>
/// Aggregating <see cref="IAudio"/> facade that resolves each focused audio service from the
/// container on demand — so lifetimes are preserved (player/monitor/devices singletons,
/// <see cref="IAudioSource"/> a fresh transient per access).
/// </summary>
/// <remarks>Named <c>AudioFacade</c> to avoid colliding with <c>Android.Media.AudioManager</c>.</remarks>
public class AudioFacade(IServiceProvider services) : IAudio
{
    public IAudioPlayer Player => services.GetRequiredService<IAudioPlayer>();
    public IAudioSource Source => services.GetRequiredService<IAudioSource>();

    public IAudioMonitor Monitor => services.GetService<IAudioMonitor>()
        ?? throw new PlatformNotSupportedException("Live audio monitoring is only available on iOS/Mac Catalyst and Android.");

    public IAudioDevices Devices => services.GetService<IAudioDevices>()
        ?? throw new PlatformNotSupportedException("Audio device enumeration is only available on iOS/Mac Catalyst and Android.");
}
