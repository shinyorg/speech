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
    public IAudioRecorder Recorder => services.GetRequiredService<IAudioRecorder>();

    public IAudioMonitor Monitor => services.GetService<IAudioMonitor>()
        ?? throw new PlatformNotSupportedException($"Live audio monitoring is only available on iOS/Mac Catalyst, Android and Linux.{LinuxHint}");

    public IAudioDevices Devices => services.GetService<IAudioDevices>()
        ?? throw new PlatformNotSupportedException($"Audio device enumeration is only available on iOS/Mac Catalyst, Android and Linux.{LinuxHint}");

    // Linux support ships in a separate package, so an unregistered service there is far more
    // likely to be a missing AddLinuxAudio() call than an unsupported platform.
    static string LinuxHint => OperatingSystem.IsLinux()
        ? " On Linux, install Shiny.Audio.Linux and call AddLinuxAudio()."
        : String.Empty;
}
