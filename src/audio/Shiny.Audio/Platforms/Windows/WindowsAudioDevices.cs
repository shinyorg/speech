using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace Shiny.Audio;

/// <summary>
/// Audio route enumeration on Windows. Capture devices come from the device enumeration API, whose
/// interface ids are exactly what <c>AudioGraph.CreateDeviceInputNodeAsync</c> takes back, so the
/// <see cref="AudioDevice.Id"/> handed out here selects that microphone for capture.
/// </summary>
/// <remarks>
/// The interface is synchronous and the enumeration API is not, so lookups block on it. They are
/// quick (a local device query), and run off the caller's thread so a UI thread can't deadlock on
/// its own continuation.
/// </remarks>
public class WindowsAudioDevices : IAudioDevices, IDisposable
{
    readonly ILogger<WindowsAudioDevices> logger;
    readonly Lock sync = new();
    DeviceWatcher? watcher;
    bool enumerated;

    public WindowsAudioDevices(ILogger<WindowsAudioDevices> logger)
    {
        this.logger = logger;
        MediaDevice.DefaultAudioCaptureDeviceChanged += this.OnDefaultChanged;
        MediaDevice.DefaultAudioRenderDeviceChanged += this.OnDefaultChanged;
    }

    event EventHandler? changed;

    public event EventHandler? Changed
    {
        add
        {
            this.changed += value;
            this.EnsureWatcher();
        }
        remove => this.changed -= value;
    }

    public IReadOnlyList<AudioDevice> GetInputs() => this.GetDevices(AudioDeviceIo.Input);
    public IReadOnlyList<AudioDevice> GetOutputs() => this.GetDevices(AudioDeviceIo.Output);

    public AudioDevice? CurrentInput => this.GetInputs().FirstOrDefault(x => x.IsCurrent);
    public AudioDevice? CurrentOutput => this.GetOutputs().FirstOrDefault(x => x.IsCurrent);

    /// <summary>No-op. Windows routes output from its own sound settings and the volume flyout.</summary>
    public Task ShowOutputPicker() => Task.CompletedTask;

    /// <summary>The capture device behind an <see cref="AudioDevice.Id"/>, or null when it is no longer present.</summary>
    internal static async Task<DeviceInformation?> FindInputAsync(string id)
    {
        try
        {
            var device = await DeviceInformation.CreateFromIdAsync(id);
            return device is { IsEnabled: true } ? device : null;
        }
        catch
        {
            return null;
        }
    }

    IReadOnlyList<AudioDevice> GetDevices(AudioDeviceIo io)
    {
        try
        {
            return Task.Run(() => QueryAsync(io)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to enumerate Windows audio devices");
            return [];
        }
    }

    static async Task<IReadOnlyList<AudioDevice>> QueryAsync(AudioDeviceIo io)
    {
        var isInput = io == AudioDeviceIo.Input;
        var selector = isInput ? MediaDevice.GetAudioCaptureSelector() : MediaDevice.GetAudioRenderSelector();
        var defaultId = isInput
            ? MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default)
            : MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);

        var devices = await DeviceInformation.FindAllAsync(selector);
        return devices
            .Where(x => x.IsEnabled)
            .Select(x => new AudioDevice(
                x.Id,
                x.Name,
                io,
                Classify(x.Name, io),
                String.Equals(x.Id, defaultId, StringComparison.OrdinalIgnoreCase)
            ))
            .ToList();
    }

    /// <summary>
    /// Classify a route from its friendly name. Windows names endpoints after their bus and form
    /// factor ("Headset Microphone (WH-1000XM4 Hands-Free)", "Microphone Array (Realtek…)"), which is
    /// enough for the coarse <see cref="AudioDeviceType"/> buckets without reading the property store.
    /// </summary>
    internal static AudioDeviceType Classify(string name, AudioDeviceIo io)
    {
        var lower = name.ToLowerInvariant();

        if (lower.Contains("hands-free") || lower.Contains("bluetooth"))
            return AudioDeviceType.Bluetooth;

        if (lower.Contains("hdmi") || lower.Contains("displayport") || lower.Contains("nvidia high definition"))
            return AudioDeviceType.Hdmi;

        if (lower.Contains("usb"))
            return AudioDeviceType.Usb;

        if (lower.Contains("headset"))
            return AudioDeviceType.WiredHeadset;

        if (lower.Contains("headphone"))
            return AudioDeviceType.WiredHeadphones;

        return io == AudioDeviceIo.Input ? AudioDeviceType.BuiltInMic : AudioDeviceType.BuiltInSpeaker;
    }

    // Plugging a device in or out. Started on first subscription: a watcher's initial enumeration
    // raises Added for every existing device, which is not a change, so events before
    // EnumerationCompleted are ignored.
    void EnsureWatcher()
    {
        lock (this.sync)
        {
            if (this.watcher != null)
                return;

            this.watcher = DeviceInformation.CreateWatcher(DeviceClass.AudioCapture);
            this.watcher.Added += (_, _) => this.RaiseAfterEnumeration();
            this.watcher.Removed += (_, _) => this.RaiseAfterEnumeration();
            this.watcher.Updated += (_, _) => this.RaiseAfterEnumeration();
            this.watcher.EnumerationCompleted += (_, _) => this.enumerated = true;
            this.watcher.Start();
        }
    }

    void RaiseAfterEnumeration()
    {
        if (this.enumerated)
            this.changed?.Invoke(this, EventArgs.Empty);
    }

    void OnDefaultChanged(object sender, object args) => this.changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        MediaDevice.DefaultAudioCaptureDeviceChanged -= this.OnDefaultChanged;
        MediaDevice.DefaultAudioRenderDeviceChanged -= this.OnDefaultChanged;

        lock (this.sync)
        {
            if (this.watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
                this.watcher.Stop();
            this.watcher = null;
        }
        GC.SuppressFinalize(this);
    }
}
