using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Audio;
using Shiny.Audio.Interop;

namespace Shiny;

public static class LinuxAudioServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Linux audio backend — capture (<see cref="IAudioSource"/>), playback
    /// (<see cref="IAudioPlayer"/>), route enumeration (<see cref="IAudioDevices"/>) and live mic
    /// monitoring (<see cref="IAudioMonitor"/>) — over PulseAudio/PipeWire, falling back to ALSA.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration is skipped entirely when the process isn't running on Linux, so this is safe to
    /// call unconditionally from shared startup code: on Windows or macOS the built-in
    /// <c>AddAudioServices()</c> registrations win and nothing here is added.
    /// </para>
    /// <para>
    /// Call it <b>before</b> <c>AddAudioServices()</c> / <c>AddSpeechServices()</c> or any
    /// <c>AddCloudSpeechToText</c> overload. Those use <c>TryAdd</c>, so whatever is registered
    /// first is what resolves — and on Linux they register nothing at all.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddLinuxAudio(this IServiceCollection services)
    {
        if (!OperatingSystem.IsLinux())
            return services;

        services.TryAddTransient<IAudioSource, LinuxAudioSource>();
        services.TryAddSingleton<IAudioPlayer, LinuxAudioPlayer>();
        services.TryAddSingleton<IAudioDevices, LinuxAudioDevices>();
        services.TryAddSingleton<IAudioMonitor, LinuxAudioMonitor>();

        // The IAudio discovery facade normally comes from AddAudioServices(); registering it here
        // means AddLinuxAudio() alone is enough for a console/daemon app.
        services.TryAddSingleton<IAudio, AudioFacade>();
        return services;
    }

    /// <summary>
    /// Whether a usable Linux audio stack was found in this process — false off Linux, and false on
    /// Linux when neither a PulseAudio/PipeWire server nor ALSA is reachable (a container with no
    /// sound devices mapped in, for instance).
    /// </summary>
    /// <remarks>
    /// Probing opens a real stream, so call it once at startup if you want to fail fast or degrade
    /// gracefully rather than surfacing a <see cref="PlatformNotSupportedException"/> on first use.
    /// </remarks>
    public static bool IsLinuxAudioAvailable => PcmStream.Backend != LinuxAudioBackend.None;
}
