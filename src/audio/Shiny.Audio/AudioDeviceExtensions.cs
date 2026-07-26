namespace Shiny.Audio;

/// <summary>
/// Classification helpers over <see cref="AudioDeviceType"/> so callers can ask "is this wired?"
/// without spelling out every variant the platforms report.
/// </summary>
public static class AudioDeviceExtensions
{
    /// <summary>
    /// True for any physically cabled route: <see cref="AudioDeviceType.WiredHeadphones"/>,
    /// <see cref="AudioDeviceType.WiredHeadset"/> and <see cref="AudioDeviceType.Usb"/>.
    /// </summary>
    /// <remarks>
    /// USB counts because on handsets without a 3.5mm jack the wired option *is* USB-C — Android
    /// reports those as <c>UsbHeadset</c>/<c>UsbDevice</c> and iOS reports digital USB-C headsets as
    /// <c>PortUsbAudio</c>, neither of which surfaces as a Wired* type. The trade-off is that a USB
    /// audio interface or DAC also answers true; use <see cref="IsHeadphones(AudioDeviceType)"/> when
    /// you specifically mean something worn on the head, at the cost of missing USB-C earbuds.
    /// </remarks>
    public static bool IsWired(this AudioDeviceType type) => type
        is AudioDeviceType.WiredHeadphones
        or AudioDeviceType.WiredHeadset
        or AudioDeviceType.Usb;

    /// <inheritdoc cref="IsWired(AudioDeviceType)"/>
    public static bool IsWired(this AudioDevice device) => device.Type.IsWired();

    /// <summary>
    /// True for <see cref="AudioDeviceType.Bluetooth"/> (HFP/SCO) and
    /// <see cref="AudioDeviceType.BluetoothA2dp"/>.
    /// </summary>
    public static bool IsBluetooth(this AudioDeviceType type) => type
        is AudioDeviceType.Bluetooth
        or AudioDeviceType.BluetoothA2dp;

    /// <inheritdoc cref="IsBluetooth(AudioDeviceType)"/>
    public static bool IsBluetooth(this AudioDevice device) => device.Type.IsBluetooth();

    /// <summary>
    /// True for the phone's own speaker or earpiece — i.e. nothing is plugged in or paired.
    /// </summary>
    public static bool IsBuiltIn(this AudioDeviceType type) => type
        is AudioDeviceType.BuiltInMic
        or AudioDeviceType.BuiltInSpeaker
        or AudioDeviceType.BuiltInReceiver;

    /// <inheritdoc cref="IsBuiltIn(AudioDeviceType)"/>
    public static bool IsBuiltIn(this AudioDevice device) => device.Type.IsBuiltIn();

    /// <summary>
    /// True for wired or Bluetooth headphones/headsets — the "audio is private to the user" check,
    /// e.g. before playing a TTS response out loud. Excludes speakers, car audio and HDMI/AirPlay.
    /// </summary>
    /// <remarks>
    /// Bluetooth cannot be narrowed further: a paired A2DP route is equally a set of earbuds or a
    /// room speaker, and neither platform distinguishes them.
    /// </remarks>
    public static bool IsHeadphones(this AudioDeviceType type) => type
        is AudioDeviceType.WiredHeadphones
        or AudioDeviceType.WiredHeadset
        or AudioDeviceType.Bluetooth
        or AudioDeviceType.BluetoothA2dp;

    /// <inheritdoc cref="IsHeadphones(AudioDeviceType)"/>
    public static bool IsHeadphones(this AudioDevice device) => device.Type.IsHeadphones();

    /// <summary>
    /// True when the route carries a microphone as well as playback — a wired *headset*
    /// (not headphones) or a Bluetooth HFP/SCO link. Useful to decide whether capture will come from
    /// the accessory or fall back to the phone's own mic.
    /// </summary>
    /// <remarks>
    /// <see cref="AudioDeviceType.Usb"/> is excluded: neither platform tells you whether a USB route
    /// has a mic from the output device alone. Check <see cref="IAudioDevices.GetInputs"/> for a
    /// matching <see cref="AudioDeviceType.Usb"/> entry instead.
    /// </remarks>
    public static bool HasMicrophone(this AudioDeviceType type) => type
        is AudioDeviceType.WiredHeadset
        or AudioDeviceType.Bluetooth;

    /// <inheritdoc cref="HasMicrophone(AudioDeviceType)"/>
    public static bool HasMicrophone(this AudioDevice device) => device.Type.HasMicrophone();
}
