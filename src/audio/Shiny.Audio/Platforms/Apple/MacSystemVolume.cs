#if MACOS
using System.Runtime.InteropServices;

namespace Shiny.Audio;

// macOS has no AVAudioSession, so the system output volume is read/written through CoreAudio's
// HAL: resolve the default output device, then get/set its "virtual main volume" (a normalized
// 0.0–1.0 Float32 that maps onto the device's real gain). Volume changes — hardware keys, the menu
// bar slider, or our own Set — arrive via AudioObject property listeners.
//
// Unlike iOS, this volume is genuinely settable. Some routes (certain HDMI / aggregate devices)
// don't expose a settable virtual main volume; CanSet reflects that for the current device.
sealed class MacSystemVolume : IDisposable
{
    const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    const uint SystemObject = 1;                          // kAudioObjectSystemObject
    const uint DefaultOutputDevice = 0x644F7574;          // 'dOut' kAudioHardwarePropertyDefaultOutputDevice
    const uint VirtualMainVolume = 0x766D7663;            // 'vmvc' kAudioHardwareServiceDeviceProperty_VirtualMainVolume
    const uint ScopeGlobal = 0x676C6F62;                 // 'glob' kAudioObjectPropertyScopeGlobal
    const uint ScopeOutput = 0x6F757470;                 // 'outp' kAudioObjectPropertyScopeOutput
    const uint ElementMain = 0;                          // kAudioObjectPropertyElementMain
    const int NoError = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    delegate int ListenerProc(uint objectId, uint numberAddresses, IntPtr addresses, IntPtr clientData);

    [DllImport(CoreAudio)]
    static extern int AudioObjectGetPropertyData(uint objectId, ref PropertyAddress address, uint qualifierDataSize, IntPtr qualifierData, ref uint dataSize, IntPtr data);

    [DllImport(CoreAudio)]
    static extern int AudioObjectSetPropertyData(uint objectId, ref PropertyAddress address, uint qualifierDataSize, IntPtr qualifierData, uint dataSize, IntPtr data);

    [DllImport(CoreAudio)]
    static extern int AudioObjectHasProperty(uint objectId, ref PropertyAddress address);

    [DllImport(CoreAudio)]
    static extern int AudioObjectIsPropertySettable(uint objectId, ref PropertyAddress address, out byte isSettable);

    [DllImport(CoreAudio)]
    static extern int AudioObjectAddPropertyListener(uint objectId, ref PropertyAddress address, ListenerProc listener, IntPtr clientData);

    [DllImport(CoreAudio)]
    static extern int AudioObjectRemovePropertyListener(uint objectId, ref PropertyAddress address, ListenerProc listener, IntPtr clientData);

    // Keep strong refs so the marshalled thunks aren't collected while CoreAudio holds them.
    readonly ListenerProc volumeListener;
    readonly ListenerProc deviceListener;
    uint deviceId;
    bool disposed;

    public MacSystemVolume()
    {
        this.volumeListener = this.OnVolumeChanged;
        this.deviceListener = this.OnDefaultDeviceChanged;

        this.deviceId = GetDefaultOutputDevice();

        var sysAddr = SystemDefaultDeviceAddress();
        AudioObjectAddPropertyListener(SystemObject, ref sysAddr, this.deviceListener, IntPtr.Zero);
        this.AddVolumeListener();
    }

    public event Action<float>? Changed;

    public bool CanSet
    {
        get
        {
            if (this.deviceId == 0)
                return false;

            var addr = VolumeAddress();
            if (AudioObjectHasProperty(this.deviceId, ref addr) == 0)
                return false;

            return AudioObjectIsPropertySettable(this.deviceId, ref addr, out var settable) == NoError && settable != 0;
        }
    }

    public float Get()
    {
        if (this.deviceId == 0)
            return 0f;

        var addr = VolumeAddress();
        if (AudioObjectHasProperty(this.deviceId, ref addr) == 0)
            return 0f;

        var size = (uint)sizeof(float);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var status = AudioObjectGetPropertyData(this.deviceId, ref addr, 0, IntPtr.Zero, ref size, buffer);
            if (status != NoError)
                return 0f;

            var value = Marshal.PtrToStructure<float>(buffer);
            return Math.Clamp(value, 0f, 1f);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Set(float value)
    {
        if (!this.CanSet)
            throw new NotSupportedException(
                "The current default output device does not expose a settable volume. Check IAudioPlayer.IsVolumeControlSupported before setting.");

        var clamped = Math.Clamp(value, 0f, 1f);
        var addr = VolumeAddress();
        var buffer = Marshal.AllocHGlobal(sizeof(float));
        try
        {
            Marshal.StructureToPtr(clamped, buffer, false);
            AudioObjectSetPropertyData(this.deviceId, ref addr, 0, IntPtr.Zero, (uint)sizeof(float), buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    void AddVolumeListener()
    {
        if (this.deviceId == 0)
            return;

        var addr = VolumeAddress();
        if (AudioObjectHasProperty(this.deviceId, ref addr) != 0)
            AudioObjectAddPropertyListener(this.deviceId, ref addr, this.volumeListener, IntPtr.Zero);
    }

    void RemoveVolumeListener()
    {
        if (this.deviceId == 0)
            return;

        var addr = VolumeAddress();
        AudioObjectRemovePropertyListener(this.deviceId, ref addr, this.volumeListener, IntPtr.Zero);
    }

    int OnVolumeChanged(uint objectId, uint numberAddresses, IntPtr addresses, IntPtr clientData)
    {
        this.Changed?.Invoke(this.Get());
        return NoError;
    }

    int OnDefaultDeviceChanged(uint objectId, uint numberAddresses, IntPtr addresses, IntPtr clientData)
    {
        // Move the volume listener onto the new default device, then report its current level.
        this.RemoveVolumeListener();
        this.deviceId = GetDefaultOutputDevice();
        this.AddVolumeListener();
        this.Changed?.Invoke(this.Get());
        return NoError;
    }

    static uint GetDefaultOutputDevice()
    {
        var addr = SystemDefaultDeviceAddress();
        var size = (uint)sizeof(uint);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var status = AudioObjectGetPropertyData(SystemObject, ref addr, 0, IntPtr.Zero, ref size, buffer);
            return status == NoError ? (uint)Marshal.PtrToStructure<uint>(buffer) : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static PropertyAddress SystemDefaultDeviceAddress()
        => new() { Selector = DefaultOutputDevice, Scope = ScopeGlobal, Element = ElementMain };

    static PropertyAddress VolumeAddress()
        => new() { Selector = VirtualMainVolume, Scope = ScopeOutput, Element = ElementMain };

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        var sysAddr = SystemDefaultDeviceAddress();
        AudioObjectRemovePropertyListener(SystemObject, ref sysAddr, this.deviceListener, IntPtr.Zero);
        this.RemoveVolumeListener();
    }
}
#endif
