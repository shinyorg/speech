namespace Shiny.Audio;

/// <summary>Whether a device captures (input) or renders (output) audio.</summary>
public enum AudioDeviceIo
{
    Input,
    Output
}

/// <summary>
/// Coarse classification of an audio route, normalized across platforms. Maps from
/// <c>AVAudioSessionPortDescription.PortType</c> (Apple) and <c>AudioDeviceInfo.Type</c> (Android).
/// </summary>
public enum AudioDeviceType
{
    Unknown,
    BuiltInMic,
    BuiltInSpeaker,
    BuiltInReceiver,
    WiredHeadset,
    WiredHeadphones,
    /// <summary>Hands-free / SCO Bluetooth (mic-capable, low quality).</summary>
    Bluetooth,
    /// <summary>Stereo A2DP Bluetooth (output only).</summary>
    BluetoothA2dp,
    Usb,
    CarAudio,
    Hdmi,
    AirPlay
}

/// <summary>
/// A single audio input or output route as reported by the OS.
/// </summary>
/// <param name="Id">Stable platform identifier (Apple port UID / Android <c>AudioDeviceInfo.Id</c>).</param>
/// <param name="Name">Human-friendly name, e.g. "JBL Flip 5" or "iPhone Microphone".</param>
/// <param name="Io">Input or output.</param>
/// <param name="Type">Normalized device type.</param>
/// <param name="IsCurrent">True when this route is the one currently in use.</param>
public record AudioDevice(
    string Id,
    string Name,
    AudioDeviceIo Io,
    AudioDeviceType Type,
    bool IsCurrent
);
