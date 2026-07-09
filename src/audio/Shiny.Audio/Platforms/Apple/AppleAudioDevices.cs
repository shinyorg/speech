using System.Linq;
using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

public class AppleAudioDevices : IAudioDevices, IDisposable
{
    readonly ILogger<AppleAudioDevices> logger;
#if !MACOS
    readonly NSObject? routeObserver;
#endif

    public AppleAudioDevices(ILogger<AppleAudioDevices> logger)
    {
        this.logger = logger;
#if !MACOS
        routeObserver = AVAudioSession.Notifications.ObserveRouteChange(
            (_, _) => Changed?.Invoke(this, EventArgs.Empty));
#endif
    }

    public event EventHandler? Changed;

    public IReadOnlyList<AudioDevice> GetInputs()
    {
#if MACOS
        return [];
#else
        var session = AVAudioSession.SharedInstance();
        var currentUids = session.CurrentRoute?.Inputs?.Select(p => p.UID).ToHashSet() ?? [];
        return (session.AvailableInputs ?? [])
            .Select(p => ToDevice(p, AudioDeviceIo.Input, currentUids.Contains(p.UID)))
            .ToList();
#endif
    }

    public IReadOnlyList<AudioDevice> GetOutputs()
    {
#if MACOS
        return [];
#else
        // iOS surfaces only the *active* output route's ports — full discovery is owned by the OS
        // route picker. So the outputs we list are, by definition, the current one(s).
        return (AVAudioSession.SharedInstance().CurrentRoute?.Outputs ?? [])
            .Select(p => ToDevice(p, AudioDeviceIo.Output, true))
            .ToList();
#endif
    }

    public AudioDevice? CurrentInput => GetInputs().FirstOrDefault(d => d.IsCurrent);
    public AudioDevice? CurrentOutput => GetOutputs().FirstOrDefault();

    // AVRoutePickerView must live in the visual tree, so the app hosts the picker button; a service
    // can't present it standalone. No-op here (documented on the interface).
    public Task ShowOutputPicker() => Task.CompletedTask;

    public void Dispose()
    {
#if !MACOS
        routeObserver?.Dispose();
#endif
        GC.SuppressFinalize(this);
    }

#if !MACOS
    static AudioDevice ToDevice(AVAudioSessionPortDescription p, AudioDeviceIo io, bool isCurrent)
        => new(p.UID, p.PortName, io, MapType(p.PortType), isCurrent);

    static AudioDeviceType MapType(string portType)
    {
        if (portType == AVAudioSession.PortBuiltInMic) return AudioDeviceType.BuiltInMic;
        if (portType == AVAudioSession.PortBuiltInSpeaker) return AudioDeviceType.BuiltInSpeaker;
        if (portType == AVAudioSession.PortBuiltInReceiver) return AudioDeviceType.BuiltInReceiver;
        if (portType == AVAudioSession.PortHeadphones) return AudioDeviceType.WiredHeadphones;
        if (portType == AVAudioSession.PortHeadsetMic) return AudioDeviceType.WiredHeadset;
        if (portType == AVAudioSession.PortBluetoothA2DP) return AudioDeviceType.BluetoothA2dp;
        if (portType == AVAudioSession.PortBluetoothHfp) return AudioDeviceType.Bluetooth;
        if (portType == AVAudioSession.PortBluetoothLE) return AudioDeviceType.Bluetooth;
        if (portType == AVAudioSession.PortUsbAudio) return AudioDeviceType.Usb;
        if (portType == AVAudioSession.PortCarAudio) return AudioDeviceType.CarAudio;
        if (portType == AVAudioSession.PortHdmi) return AudioDeviceType.Hdmi;
        if (portType == AVAudioSession.PortAirPlay) return AudioDeviceType.AirPlay;
        return AudioDeviceType.Unknown;
    }
#endif
}
