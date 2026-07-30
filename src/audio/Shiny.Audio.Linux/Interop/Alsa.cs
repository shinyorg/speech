using System.Runtime.InteropServices;

namespace Shiny.Audio.Interop;

/// <summary>
/// Bindings for ALSA (<c>libasound</c>) — the fallback when no PulseAudio/PipeWire server is
/// reachable (headless servers, minimal containers, Raspberry Pi images).
/// </summary>
/// <remarks>
/// Every stream is opened against the <c>default</c> device, which routes through ALSA's
/// <c>plug</c> plugin. That plugin converts format, rate and channel count on our behalf, so — as
/// with PulseAudio — we can ask for exactly 16kHz mono S16LE (or a decoded file's native rate) and
/// let the layer below deal with hardware that runs at 48kHz stereo.
/// </remarks>
static partial class Alsa
{
    const string Lib = "libasound.so.2";

    internal const int StreamPlayback = 0;
    internal const int StreamCapture = 1;

    /// <summary>SND_PCM_FORMAT_S16_LE.</summary>
    internal const int FormatS16Le = 2;

    /// <summary>SND_PCM_ACCESS_RW_INTERLEAVED.</summary>
    internal const int AccessRwInterleaved = 3;

    internal const int ErrorPipe = -32;   // -EPIPE: xrun (overrun on capture, underrun on playback)

    [LibraryImport(Lib, EntryPoint = "snd_pcm_open", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PcmOpen(out IntPtr pcm, string name, int stream, int mode);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_close")]
    internal static partial int PcmClose(IntPtr pcm);

    /// <summary>
    /// The high-level configuration helper — it does the whole <c>hw_params</c>/<c>sw_params</c>
    /// dance internally. <paramref name="softResample"/> = 1 enables ALSA's own rate conversion.
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "snd_pcm_set_params")]
    internal static partial int PcmSetParams(
        IntPtr pcm,
        int format,
        int access,
        uint channels,
        uint rate,
        int softResample,
        uint latencyMicroseconds
    );

    [LibraryImport(Lib, EntryPoint = "snd_pcm_readi")]
    internal static partial nint PcmReadInterleaved(IntPtr pcm, ref byte buffer, nuint frames);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_writei")]
    internal static partial nint PcmWriteInterleaved(IntPtr pcm, ref byte buffer, nuint frames);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_recover")]
    internal static partial int PcmRecover(IntPtr pcm, int error, int silent);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_drain")]
    internal static partial int PcmDrain(IntPtr pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_drop")]
    internal static partial int PcmDrop(IntPtr pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_prepare")]
    internal static partial int PcmPrepare(IntPtr pcm);

    [LibraryImport(Lib, EntryPoint = "snd_device_name_hint", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int DeviceNameHint(int card, string iface, out IntPtr hints);

    [LibraryImport(Lib, EntryPoint = "snd_device_name_get_hint", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr DeviceNameGetHint(IntPtr hint, string id);

    [LibraryImport(Lib, EntryPoint = "snd_device_name_free_hint")]
    internal static partial int DeviceNameFreeHint(IntPtr hints);

    [LibraryImport(Lib, EntryPoint = "snd_strerror")]
    private static partial IntPtr StrErrorRaw(int error);

    internal static string StrError(int error)
    {
        try
        {
            return Marshal.PtrToStringUTF8(StrErrorRaw(error)) ?? $"ALSA error {error}";
        }
        catch (DllNotFoundException)
        {
            return $"ALSA error {error}";
        }
    }

    /// <summary>True when libasound is present and the <c>default</c> device can be opened.</summary>
    internal static bool IsAvailable => available ??= Probe();
    static bool? available;

    static bool Probe()
    {
        try
        {
            if (PcmOpen(out var pcm, "default", StreamPlayback, 0) < 0)
                return false;

            PcmClose(pcm);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Read a hint string and release the buffer ALSA malloc'd for it. <c>snd_device_name_get_hint</c>
    /// documents the caller as owner, so the buffer must go back to libc's allocator — not ALSA's.
    /// </summary>
    internal static string? ReadHint(IntPtr hint, string id)
    {
        var ptr = DeviceNameGetHint(hint, id);
        if (ptr == IntPtr.Zero)
            return null;

        var value = Marshal.PtrToStringUTF8(ptr);
        Libc.Free(ptr);
        return value;
    }
}

/// <summary>
/// The single libc entry point this library needs, for buffers ALSA hands over with malloc'd
/// ownership.
/// </summary>
static partial class Libc
{
    [LibraryImport("libc.so.6", EntryPoint = "free")]
    private static partial void FreeRaw(IntPtr ptr);

    // glibc is `libc.so.6`; musl (Alpine) names it differently and has no such soname. Rather than
    // guess at every libc, probe once and — where the symbol is unreachable — accept leaking the
    // handful of short strings a device enumeration allocates. Enumeration is a rare call, and
    // leaking is strictly better than calling free() through the wrong allocator.
    static bool? freeAvailable;

    internal static void Free(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero || freeAvailable == false)
            return;

        try
        {
            FreeRaw(ptr);
            freeAvailable = true;
        }
        catch (DllNotFoundException)
        {
            freeAvailable = false;
        }
        catch (EntryPointNotFoundException)
        {
            freeAvailable = false;
        }
    }
}
