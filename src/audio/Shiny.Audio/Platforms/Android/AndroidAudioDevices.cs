using System.Linq;
using Android.Content;
using Android.Media;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

public class AndroidAudioDevices : IAudioDevices, IDisposable
{
    readonly ILogger<AndroidAudioDevices> logger;
    readonly AudioManager? audioManager;
    DeviceCallback? callback;

    public AndroidAudioDevices(ILogger<AndroidAudioDevices> logger)
    {
        this.logger = logger;
        audioManager = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);
        if (audioManager != null)
        {
            callback = new DeviceCallback(() => Changed?.Invoke(this, EventArgs.Empty));
            audioManager.RegisterAudioDeviceCallback(callback, null);
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<AudioDevice> GetInputs() => Enumerate(GetDevicesTargets.Inputs, AudioDeviceIo.Input);
    public IReadOnlyList<AudioDevice> GetOutputs() => Enumerate(GetDevicesTargets.Outputs, AudioDeviceIo.Output);

    public AudioDevice? CurrentInput => GetInputs().FirstOrDefault(d => d.IsCurrent);
    public AudioDevice? CurrentOutput => GetOutputs().FirstOrDefault(d => d.IsCurrent);

    // Android has no single app-launchable system output picker; selection is applied per-stream
    // via IAudioMonitor.SetOutputDevice. Documented no-op.
    public Task ShowOutputPicker() => Task.CompletedTask;

    public void Dispose()
    {
        if (audioManager != null && callback != null)
            audioManager.UnregisterAudioDeviceCallback(callback);
        callback?.Dispose();
        callback = null;
        GC.SuppressFinalize(this);
    }

    List<AudioDevice> Enumerate(GetDevicesTargets target, AudioDeviceIo io)
    {
        var result = new List<AudioDevice>();
        var devices = audioManager?.GetDevices(target);
        if (devices == null)
            return result;

        // "Current" is a derived value on Android — AudioDeviceInfo carries no active flag. Rank the
        // connected outputs the way the platform's own routing policy does and take the winner. Match
        // on device Id, not type, so two routes of the same type don't both report as current.
        var currentId = io == AudioDeviceIo.Output ? PickCurrentOutput(devices)?.Id : null;

        foreach (var d in devices)
        {
            var type = MapType(d.Type);
            var name = d.ProductName?.ToString() ?? type.ToString();
            var isCurrent = currentId != null && d.Id == currentId;
            result.Add(new AudioDevice(d.Id.ToString(), name, io, type, isCurrent));
        }
        return result;
    }

    // Previously this read AudioManager.BluetoothA2dpOn / WiredHeadsetOn. Both are deprecated, and
    // WiredHeadsetOn is true for a headset *and* headphones while only ever reporting the latter — so a
    // mic-equipped wired headset matched nothing and CurrentOutput came back null. USB-C headsets (the
    // only wired option on most current handsets) missed for the same reason. Scan the real device list.
    static AudioDeviceInfo? PickCurrentOutput(IEnumerable<AudioDeviceInfo> outputs)
        => outputs.OrderBy(d => RoutePriority(MapType(d.Type))).FirstOrDefault();

    // Lower wins. An attached external transport takes the route from the built-ins; the earpiece is
    // last because it is only selected for in-call use, never as a general media route.
    static int RoutePriority(AudioDeviceType type) => type switch
    {
        AudioDeviceType.BluetoothA2dp => 0,
        AudioDeviceType.Bluetooth => 1,
        AudioDeviceType.WiredHeadset => 2,
        AudioDeviceType.WiredHeadphones => 3,
        AudioDeviceType.Usb => 4,
        AudioDeviceType.CarAudio => 5,
        AudioDeviceType.Hdmi => 6,
        AudioDeviceType.AirPlay => 7,
        AudioDeviceType.BuiltInSpeaker => 8,
        AudioDeviceType.BuiltInReceiver => 9,
        _ => 10
    };

    static AudioDeviceType MapType(Android.Media.AudioDeviceType t) => t switch
    {
        Android.Media.AudioDeviceType.BuiltinMic => AudioDeviceType.BuiltInMic,
        Android.Media.AudioDeviceType.BuiltinSpeaker => AudioDeviceType.BuiltInSpeaker,
        // API 30+ value; harmless to match on older devices since they simply never report it.
#pragma warning disable CA1416
        Android.Media.AudioDeviceType.BuiltinSpeakerSafe => AudioDeviceType.BuiltInSpeaker,
#pragma warning restore CA1416
        Android.Media.AudioDeviceType.BuiltinEarpiece => AudioDeviceType.BuiltInReceiver,
        Android.Media.AudioDeviceType.WiredHeadset => AudioDeviceType.WiredHeadset,
        Android.Media.AudioDeviceType.WiredHeadphones => AudioDeviceType.WiredHeadphones,
        Android.Media.AudioDeviceType.BluetoothA2dp => AudioDeviceType.BluetoothA2dp,
        Android.Media.AudioDeviceType.BluetoothSco => AudioDeviceType.Bluetooth,
        Android.Media.AudioDeviceType.UsbDevice => AudioDeviceType.Usb,
        Android.Media.AudioDeviceType.UsbHeadset => AudioDeviceType.Usb,
        Android.Media.AudioDeviceType.UsbAccessory => AudioDeviceType.Usb,
        Android.Media.AudioDeviceType.Hdmi => AudioDeviceType.Hdmi,
        _ => AudioDeviceType.Unknown
    };

    sealed class DeviceCallback(Action onChanged) : AudioDeviceCallback
    {
        public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices) => onChanged();
        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices) => onChanged();
    }
}
