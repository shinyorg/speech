using System.Runtime.InteropServices;

namespace Shiny.Audio;

// Windows exposes the default render endpoint's master volume through the Core Audio (WASAPI) COM API.
// We resolve the default multimedia render endpoint, activate IAudioEndpointVolume on it, and use the
// scalar (normalized 0.0–1.0) get/set. An IAudioEndpointVolumeCallback delivers changes from the volume
// keys, the system slider, or our own Set — mirroring the KVO / content-observer paths on Apple/Android.
//
// The endpoint is captured at construction (the current default render device). A later default-device
// switch is not tracked; recreate the player to pick up a new default device.
sealed class WindowsSystemVolume : IDisposable
{
    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    const int EDataFlow_Render = 0;
    const int ERole_Multimedia = 1;
    const int CLSCTX_ALL = 0x17;

    readonly IAudioEndpointVolume? endpointVolume;
    readonly VolumeCallback? callback;
    Guid eventContext = Guid.NewGuid();
    bool disposed;

    public WindowsSystemVolume()
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
            Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;

        if (enumerator.GetDefaultAudioEndpoint(EDataFlow_Render, ERole_Multimedia, out var device) != 0 || device == null)
            return;

        var iid = IID_IAudioEndpointVolume;
        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) != 0 || obj is not IAudioEndpointVolume ev)
            return;

        this.endpointVolume = ev;
        this.callback = new VolumeCallback(v => this.Changed?.Invoke(v));
        this.endpointVolume.RegisterControlChangeNotify(this.callback);
    }

    public event Action<float>? Changed;

    public bool CanSet => this.endpointVolume != null;

    public float Get()
    {
        if (this.endpointVolume == null)
            return 0f;

        return this.endpointVolume.GetMasterVolumeLevelScalar(out var level) == 0
            ? Math.Clamp(level, 0f, 1f)
            : 0f;
    }

    public void Set(float value)
    {
        if (this.endpointVolume == null)
            throw new NotSupportedException(
                "No default render endpoint is available to set the system volume. Check IAudioPlayer.IsVolumeControlSupported before setting.");

        // Passing our own event context still fires the callback (we want VolumeChanged on our own set too).
        this.endpointVolume.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref this.eventContext);
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        if (this.endpointVolume != null && this.callback != null)
            this.endpointVolume.UnregisterControlChangeNotify(this.callback);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr notifyData);
    }

    // AUDIO_VOLUME_NOTIFICATION_DATA — we only need the master scalar. Layout: Guid(16) + BOOL(4) + float + ...
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct VolumeNotificationData
    {
        public Guid EventContext;
        public int Muted;
        public float MasterVolume;
        public uint Channels;
        public float FirstChannelVolume;
    }

    [ComVisible(true)]
    sealed class VolumeCallback(Action<float> onChanged) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr notifyData)
        {
            if (notifyData != IntPtr.Zero)
            {
                var data = Marshal.PtrToStructure<VolumeNotificationData>(notifyData);
                onChanged(Math.Clamp(data.MasterVolume, 0f, 1f));
            }
            return 0;
        }
    }
}
