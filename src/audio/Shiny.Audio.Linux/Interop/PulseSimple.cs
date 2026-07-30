using System.Runtime.InteropServices;

namespace Shiny.Audio.Interop;

/// <summary>
/// Bindings for PulseAudio's <c>simple</c> API — a blocking read/write interface that is all a
/// capture/playback pump needs. Works unchanged on PipeWire via its <c>pipewire-pulse</c> shim,
/// which is how most current desktops present themselves.
/// </summary>
/// <remarks>
/// The server resamples and remixes for us, so a stream can be opened at exactly the rate and
/// channel count we want (16kHz mono S16LE for the <see cref="IAudioSource"/> contract, or a
/// decoded file's native rate for playback) regardless of what the hardware runs at. That is why
/// this library carries no resampler.
/// </remarks>
static partial class PulseSimple
{
    const string Lib = "libpulse-simple.so.0";
    const string LibPulse = "libpulse.so.0";

    internal const int DirectionPlayback = 1;
    internal const int DirectionRecord = 2;

    /// <summary>PA_SAMPLE_S16LE — the only format this library produces or consumes.</summary>
    internal const int SampleFormatS16Le = 3;

    [LibraryImport(Lib, EntryPoint = "pa_simple_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr New(
        string? server,
        string appName,
        int direction,
        string? device,
        string streamName,
        in PaSampleSpec sampleSpec,
        IntPtr channelMap,
        IntPtr bufferAttr,
        out int error
    );

    [LibraryImport(Lib, EntryPoint = "pa_simple_read")]
    internal static partial int Read(IntPtr stream, ref byte data, nuint bytes, out int error);

    [LibraryImport(Lib, EntryPoint = "pa_simple_write")]
    internal static partial int Write(IntPtr stream, ref byte data, nuint bytes, out int error);

    [LibraryImport(Lib, EntryPoint = "pa_simple_drain")]
    internal static partial int Drain(IntPtr stream, out int error);

    [LibraryImport(Lib, EntryPoint = "pa_simple_flush")]
    internal static partial int Flush(IntPtr stream, out int error);

    [LibraryImport(Lib, EntryPoint = "pa_simple_free")]
    internal static partial void Free(IntPtr stream);

    [LibraryImport(LibPulse, EntryPoint = "pa_strerror")]
    private static partial IntPtr StrErrorRaw(int error);

    internal static string StrError(int error)
    {
        try
        {
            return Marshal.PtrToStringUTF8(StrErrorRaw(error)) ?? $"PulseAudio error {error}";
        }
        catch (DllNotFoundException)
        {
            return $"PulseAudio error {error}";
        }
    }

    /// <summary>
    /// True when libpulse-simple can be loaded and a stream can actually be opened — i.e. a
    /// PulseAudio or PipeWire server is running and reachable. Probed once; a headless box or a
    /// minimal container has the library absent (or no server) and falls back to ALSA.
    /// </summary>
    internal static bool IsAvailable => available ??= Probe();
    static bool? available;

    static bool Probe()
    {
        try
        {
            // Opening a playback stream is the only honest test: the library can be present while
            // no server is running, and pa_simple_new is what fails in that case.
            var spec = new PaSampleSpec { Format = SampleFormatS16Le, Rate = 16000, Channels = 1 };
            var handle = New(null, "Shiny", DirectionPlayback, null, "probe", in spec, IntPtr.Zero, IntPtr.Zero, out _);
            if (handle == IntPtr.Zero)
                return false;

            Free(handle);
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
}

/// <summary>
/// <c>pa_sample_spec</c>. Blittable and laid out to match: a 4-byte format enum, a 4-byte rate and
/// a single byte channel count, which the C compiler pads out to 12 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
struct PaSampleSpec
{
    public int Format;
    public uint Rate;
    public byte Channels;
    byte pad0;
    byte pad1;
    byte pad2;
}
