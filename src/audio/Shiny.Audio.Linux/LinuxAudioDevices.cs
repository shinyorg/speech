using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shiny.Audio.Interop;

namespace Shiny.Audio;

/// <summary>
/// Audio route enumeration on Linux. PulseAudio/PipeWire reports sinks and sources along with the
/// server's current defaults; the ALSA fallback lists PCM device hints.
/// </summary>
public class LinuxAudioDevices(ILogger<LinuxAudioDevices> logger) : IAudioDevices, IDisposable
{
    readonly Lock sync = new();
    IDisposable? subscription;

    public IReadOnlyList<AudioDevice> GetInputs() => this.GetDevices(AudioDeviceIo.Input);
    public IReadOnlyList<AudioDevice> GetOutputs() => this.GetDevices(AudioDeviceIo.Output);

    public AudioDevice? CurrentInput => this.GetInputs().FirstOrDefault(x => x.IsCurrent);
    public AudioDevice? CurrentOutput => this.GetOutputs().FirstOrDefault(x => x.IsCurrent);

    /// <summary>
    /// No-op. Linux has no system-wide route picker equivalent to iOS's <c>AVRoutePickerView</c> —
    /// routing lives in the desktop environment's own sound settings.
    /// </summary>
    public Task ShowOutputPicker() => Task.CompletedTask;

    event EventHandler? changed;

    public event EventHandler? Changed
    {
        add
        {
            this.changed += value;
            this.EnsureSubscription();
        }
        remove => this.changed -= value;
    }

    void EnsureSubscription()
    {
        lock (this.sync)
        {
            if (this.subscription != null || PcmStream.Backend != LinuxAudioBackend.PulseAudio)
                return;

            // ALSA on its own has no change notification, so Changed simply never fires there.
            this.subscription = PulseIntrospect.Subscribe(() => this.changed?.Invoke(this, EventArgs.Empty));
        }
    }

    IReadOnlyList<AudioDevice> GetDevices(AudioDeviceIo io)
    {
        try
        {
            return PcmStream.Backend switch
            {
                LinuxAudioBackend.PulseAudio => GetPulseDevices(io),
                LinuxAudioBackend.Alsa => GetAlsaDevices(io),
                _ => []
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enumerate Linux audio devices");
            return [];
        }
    }

    static IReadOnlyList<AudioDevice> GetPulseDevices(AudioDeviceIo io)
    {
        var isInput = io == AudioDeviceIo.Input;
        var devices = PulseIntrospect.GetDevices(out var defaultSink, out var defaultSource);
        var currentName = isInput ? defaultSource : defaultSink;

        return devices
            .Where(x => x.IsInput == isInput)
            .Select(x => new AudioDevice(
                x.Name,
                x.Description,
                io,
                ClassifyByName(x.Name, io),
                x.Name == currentName
            ))
            .ToList();
    }

    static IReadOnlyList<AudioDevice> GetAlsaDevices(AudioDeviceIo io)
    {
        if (Alsa.DeviceNameHint(-1, "pcm", out var hints) < 0 || hints == IntPtr.Zero)
            return [];

        var wanted = io == AudioDeviceIo.Input ? "Input" : "Output";
        var results = new List<AudioDevice>();

        try
        {
            // A null-terminated array of opaque hint pointers.
            for (var i = 0; ; i++)
            {
                var hint = Marshal.ReadIntPtr(hints, i * IntPtr.Size);
                if (hint == IntPtr.Zero)
                    break;

                var name = Alsa.ReadHint(hint, "NAME");
                if (name == null)
                    continue;

                // IOID is null for devices that do both directions, so they belong in each list.
                var direction = Alsa.ReadHint(hint, "IOID");
                if (direction != null && direction != wanted)
                    continue;

                var description = Alsa.ReadHint(hint, "DESC")?.Replace('\n', ' ') ?? name;

                results.Add(new AudioDevice(
                    name,
                    description,
                    io,
                    ClassifyByName(name, io),
                    name == "default"
                ));
            }
        }
        finally
        {
            Alsa.DeviceNameFreeHint(hints);
        }

        return results;
    }

    /// <summary>
    /// Classify a route from its device name. Both stacks encode the bus in the name
    /// (<c>alsa_input.usb-…</c>, <c>bluez_output.…a2dp_sink</c>, <c>…hdmi-stereo</c>), which is
    /// enough for the coarse <see cref="AudioDeviceType"/> buckets and avoids walking PulseAudio's
    /// property list through raw pointers.
    /// </summary>
    static AudioDeviceType ClassifyByName(string name, AudioDeviceIo io)
    {
        var lower = name.ToLowerInvariant();

        if (lower.Contains("bluez") || lower.Contains("bluetooth"))
            return lower.Contains("a2dp") ? AudioDeviceType.BluetoothA2dp : AudioDeviceType.Bluetooth;

        if (lower.Contains("hdmi") || lower.Contains("displayport"))
            return AudioDeviceType.Hdmi;

        if (lower.Contains("usb"))
            return AudioDeviceType.Usb;

        if (lower.Contains("headset"))
            return AudioDeviceType.WiredHeadset;

        if (lower.Contains("headphone"))
            return AudioDeviceType.WiredHeadphones;

        return io == AudioDeviceIo.Input ? AudioDeviceType.BuiltInMic : AudioDeviceType.BuiltInSpeaker;
    }

    public void Dispose()
    {
        this.subscription?.Dispose();
        this.subscription = null;
        GC.SuppressFinalize(this);
    }
}
