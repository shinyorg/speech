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

        // "Current" is a heuristic on Android (no per-device active flag): match the highest-priority
        // connected route for outputs. Inputs follow the current output's transport where relevant.
        var currentOutput = CurrentOutputType();

        foreach (var d in devices)
        {
            var type = MapType(d.Type);
            var name = d.ProductName?.ToString() ?? type.ToString();
            var isCurrent = io == AudioDeviceIo.Output && type == currentOutput;
            result.Add(new AudioDevice(d.Id.ToString(), name, io, type, isCurrent));
        }
        return result;
    }

    AudioDeviceType CurrentOutputType()
    {
        if (audioManager == null)
            return AudioDeviceType.BuiltInSpeaker;

#pragma warning disable CA1422 // route-state getters are deprecated but still the simplest signal
        if (audioManager.BluetoothA2dpOn)
            return AudioDeviceType.BluetoothA2dp;
        if (audioManager.WiredHeadsetOn)
            return AudioDeviceType.WiredHeadphones;
#pragma warning restore CA1422
        return AudioDeviceType.BuiltInSpeaker;
    }

    static AudioDeviceType MapType(Android.Media.AudioDeviceType t) => t switch
    {
        Android.Media.AudioDeviceType.BuiltinMic => AudioDeviceType.BuiltInMic,
        Android.Media.AudioDeviceType.BuiltinSpeaker => AudioDeviceType.BuiltInSpeaker,
        Android.Media.AudioDeviceType.BuiltinSpeakerSafe => AudioDeviceType.BuiltInSpeaker,
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
