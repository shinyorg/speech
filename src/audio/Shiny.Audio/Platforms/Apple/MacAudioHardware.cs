#if MACOS
using System.Linq;
using System.Runtime.InteropServices;
using CoreFoundation;
using ObjCRuntime;

namespace Shiny.Audio;

// macOS has no AVAudioSession, so its microphones come straight from CoreAudio's HAL: every device
// object with at least one input stream is a capture device. Its UID is the stable identifier (it
// survives unplugging and reboots, unlike the AudioDeviceID, which is reassigned), so that is what
// AudioDevice.Id carries; it is resolved back to the live AudioDeviceID when capture starts.
static class MacAudioHardware
{
    const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    const uint SystemObject = 1;                          // kAudioObjectSystemObject
    const uint Devices = 0x64657623;                      // 'dev#' kAudioHardwarePropertyDevices
    const uint DefaultInputDevice = 0x64496E20;           // 'dIn ' kAudioHardwarePropertyDefaultInputDevice
    const uint Streams = 0x73746D23;                      // 'stm#' kAudioDevicePropertyStreams
    const uint Name = 0x6C6E616D;                         // 'lnam' kAudioObjectPropertyName
    const uint DeviceUid = 0x75696420;                    // 'uid ' kAudioDevicePropertyDeviceUID
    const uint TransportType = 0x7472616E;                // 'tran' kAudioDevicePropertyTransportType
    const uint ScopeGlobal = 0x676C6F62;                  // 'glob' kAudioObjectPropertyScopeGlobal
    const uint ScopeInput = 0x696E7074;                   // 'inpt' kAudioObjectPropertyScopeInput
    const uint ElementMain = 0;                           // kAudioObjectPropertyElementMain
    const int NoError = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    public delegate int ListenerProc(uint objectId, uint numberAddresses, IntPtr addresses, IntPtr clientData);

    [DllImport(CoreAudio)]
    static extern int AudioObjectGetPropertyDataSize(uint objectId, ref PropertyAddress address, uint qualifierDataSize, IntPtr qualifierData, out uint dataSize);

    [DllImport(CoreAudio)]
    static extern int AudioObjectGetPropertyData(uint objectId, ref PropertyAddress address, uint qualifierDataSize, IntPtr qualifierData, ref uint dataSize, IntPtr data);

    [DllImport(CoreAudio)]
    static extern int AudioObjectAddPropertyListener(uint objectId, ref PropertyAddress address, ListenerProc listener, IntPtr clientData);

    [DllImport(CoreAudio)]
    static extern int AudioObjectRemovePropertyListener(uint objectId, ref PropertyAddress address, ListenerProc listener, IntPtr clientData);

    /// <summary>Every device with an input stream, the system default flagged as current.</summary>
    public static IReadOnlyList<AudioDevice> GetInputs()
    {
        var defaultId = GetDefaultInputId();
        var results = new List<AudioDevice>();

        foreach (var id in GetDeviceIds())
        {
            if (!HasInputStreams(id))
                continue;

            var uid = GetString(id, DeviceUid);
            if (uid == null)
                continue;

            results.Add(new AudioDevice(
                uid,
                GetString(id, Name) ?? uid,
                AudioDeviceIo.Input,
                MapTransport(GetUInt(id, TransportType, ScopeGlobal)),
                id == defaultId
            ));
        }
        return results;
    }

    /// <summary>The live AudioDeviceID behind a UID, or 0 when that device is no longer connected.</summary>
    public static uint FindDevice(string uid)
    {
        foreach (var id in GetDeviceIds())
        {
            if (HasInputStreams(id) && GetString(id, DeviceUid) == uid)
                return id;
        }
        return 0;
    }

    /// <summary>
    /// Calls <paramref name="listener"/> when devices come and go or the default input changes, on a
    /// CoreAudio thread. The caller keeps the delegate alive and passes the same one to
    /// <see cref="StopListening"/>.
    /// </summary>
    public static void Listen(ListenerProc listener)
    {
        var devices = Address(Devices, ScopeGlobal);
        var defaultInput = Address(DefaultInputDevice, ScopeGlobal);
        AudioObjectAddPropertyListener(SystemObject, ref devices, listener, IntPtr.Zero);
        AudioObjectAddPropertyListener(SystemObject, ref defaultInput, listener, IntPtr.Zero);
    }

    public static void StopListening(ListenerProc listener)
    {
        var devices = Address(Devices, ScopeGlobal);
        var defaultInput = Address(DefaultInputDevice, ScopeGlobal);
        AudioObjectRemovePropertyListener(SystemObject, ref devices, listener, IntPtr.Zero);
        AudioObjectRemovePropertyListener(SystemObject, ref defaultInput, listener, IntPtr.Zero);
    }

    /// <summary>The live AudioDeviceID of the system default input, or 0 when there is none.</summary>
    public static uint GetDefaultInputId() => GetUInt(SystemObject, DefaultInputDevice, ScopeGlobal);

    static uint[] GetDeviceIds()
    {
        var address = Address(Devices, ScopeGlobal);
        if (AudioObjectGetPropertyDataSize(SystemObject, ref address, 0, IntPtr.Zero, out var size) != NoError || size == 0)
            return [];

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(SystemObject, ref address, 0, IntPtr.Zero, ref size, buffer) != NoError)
                return [];

            var ids = new int[size / sizeof(uint)];
            Marshal.Copy(buffer, ids, 0, ids.Length);
            return ids.Select(x => (uint)x).ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Output-only devices report an empty stream list on the input scope.
    static bool HasInputStreams(uint id)
    {
        var address = Address(Streams, ScopeInput);
        return AudioObjectGetPropertyDataSize(id, ref address, 0, IntPtr.Zero, out var size) == NoError && size > 0;
    }

    static uint GetUInt(uint objectId, uint selector, uint scope)
    {
        var address = Address(selector, scope);
        var size = (uint)sizeof(uint);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, buffer) == NoError
                ? (uint)Marshal.ReadInt32(buffer)
                : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Name and UID come back as a CFStringRef the caller owns.
    static string? GetString(uint objectId, uint selector)
    {
        var address = Address(selector, ScopeGlobal);
        var size = (uint)IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, buffer) != NoError)
                return null;

            var handle = Marshal.ReadIntPtr(buffer);
            return handle == IntPtr.Zero ? null : CFString.FromHandle(new NativeHandle(handle), true);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static AudioDeviceType MapTransport(uint transport) => transport switch
    {
        0x626C746E => AudioDeviceType.BuiltInMic,         // 'bltn'
        0x75736220 => AudioDeviceType.Usb,                // 'usb '
        0x626C7565 or 0x626C6561 => AudioDeviceType.Bluetooth, // 'blue' / 'blea'
        0x68646D69 or 0x64707274 => AudioDeviceType.Hdmi,     // 'hdmi' / 'dprt'
        0x61697270 => AudioDeviceType.AirPlay,            // 'airp'
        _ => AudioDeviceType.Unknown
    };

    static PropertyAddress Address(uint selector, uint scope) => new() { Selector = selector, Scope = scope, Element = ElementMain };
}
#endif
