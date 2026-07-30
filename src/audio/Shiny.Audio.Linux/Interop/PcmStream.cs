namespace Shiny.Audio.Interop;

/// <summary>Which native audio stack the process is talking to.</summary>
enum LinuxAudioBackend
{
    /// <summary>No usable audio stack was found.</summary>
    None,

    /// <summary>PulseAudio, or PipeWire through its <c>pipewire-pulse</c> compatibility layer.</summary>
    PulseAudio,

    /// <summary>ALSA directly — the fallback for headless servers and minimal containers.</summary>
    Alsa
}

/// <summary>
/// A blocking interleaved S16LE PCM stream over whichever backend is available.
/// </summary>
/// <remarks>
/// Both backends convert rate/channel count below us — PulseAudio server-side, ALSA through the
/// <c>plug</c> plugin on the <c>default</c> device — so a stream can be opened at whatever format
/// the caller wants and the hardware's real format never leaks upward.
/// </remarks>
abstract class PcmStream : IDisposable
{
    /// <summary>The backend in use, probed once per process.</summary>
    internal static LinuxAudioBackend Backend => backend ??= Probe();
    static LinuxAudioBackend? backend;

    static LinuxAudioBackend Probe()
    {
        if (!OperatingSystem.IsLinux())
            return LinuxAudioBackend.None;

        if (PulseSimple.IsAvailable)
            return LinuxAudioBackend.PulseAudio;

        return Alsa.IsAvailable ? LinuxAudioBackend.Alsa : LinuxAudioBackend.None;
    }

    internal int SampleRate { get; private protected init; }
    internal int Channels { get; private protected init; }

    internal int BytesPerFrame => this.Channels * 2;

    /// <summary>Open a capture stream. <paramref name="device"/> null selects the OS default.</summary>
    internal static PcmStream OpenCapture(int sampleRate, int channels, string? device) => Backend switch
    {
        LinuxAudioBackend.PulseAudio => new PulsePcmStream(sampleRate, channels, device, capture: true),
        LinuxAudioBackend.Alsa => new AlsaPcmStream(sampleRate, channels, device, capture: true),
        _ => throw new PlatformNotSupportedException(NoBackendMessage)
    };

    /// <summary>Open a playback stream. <paramref name="device"/> null selects the OS default.</summary>
    internal static PcmStream OpenPlayback(int sampleRate, int channels, string? device) => Backend switch
    {
        LinuxAudioBackend.PulseAudio => new PulsePcmStream(sampleRate, channels, device, capture: false),
        LinuxAudioBackend.Alsa => new AlsaPcmStream(sampleRate, channels, device, capture: false),
        _ => throw new PlatformNotSupportedException(NoBackendMessage)
    };

    internal const string NoBackendMessage =
        "No Linux audio backend is available. Install PulseAudio or PipeWire (libpulse-simple.so.0) " +
        "for desktop audio, or ALSA (libasound.so.2) for direct hardware access.";

    /// <summary>Read up to <paramref name="count"/> bytes of PCM. Returns the bytes actually read.</summary>
    internal abstract int Read(byte[] buffer, int count);

    /// <summary>Write <paramref name="count"/> bytes of PCM, blocking until the backend accepts all of it.</summary>
    internal abstract void Write(byte[] buffer, int offset, int count);

    /// <summary>Block until buffered playback audio has actually been rendered.</summary>
    internal abstract void Drain();

    public abstract void Dispose();
}

sealed class PulsePcmStream : PcmStream
{
    IntPtr handle;

    internal PulsePcmStream(int sampleRate, int channels, string? device, bool capture)
    {
        this.SampleRate = sampleRate;
        this.Channels = channels;

        var spec = new PaSampleSpec
        {
            Format = PulseSimple.SampleFormatS16Le,
            Rate = (uint)sampleRate,
            Channels = (byte)channels
        };

        this.handle = PulseSimple.New(
            null,
            "Shiny.Audio",
            capture ? PulseSimple.DirectionRecord : PulseSimple.DirectionPlayback,
            device,
            capture ? "capture" : "playback",
            in spec,
            IntPtr.Zero,
            IntPtr.Zero,
            out var error
        );

        if (this.handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to open PulseAudio {(capture ? "capture" : "playback")} stream: {PulseSimple.StrError(error)}");
    }

    internal override int Read(byte[] buffer, int count)
    {
        if (this.handle == IntPtr.Zero)
            return 0;

        // pa_simple_read blocks until the full request is satisfied — a short read means failure.
        if (PulseSimple.Read(this.handle, ref buffer[0], (nuint)count, out var error) < 0)
            throw new IOException($"PulseAudio capture read failed: {PulseSimple.StrError(error)}");

        return count;
    }

    internal override void Write(byte[] buffer, int offset, int count)
    {
        if (this.handle == IntPtr.Zero)
            return;

        if (PulseSimple.Write(this.handle, ref buffer[offset], (nuint)count, out var error) < 0)
            throw new IOException($"PulseAudio playback write failed: {PulseSimple.StrError(error)}");
    }

    internal override void Drain()
    {
        if (this.handle != IntPtr.Zero)
            PulseSimple.Drain(this.handle, out _);
    }

    public override void Dispose()
    {
        if (this.handle == IntPtr.Zero)
            return;

        PulseSimple.Free(this.handle);
        this.handle = IntPtr.Zero;
    }
}

sealed class AlsaPcmStream : PcmStream
{
    /// <summary>Target buffer latency handed to <c>snd_pcm_set_params</c>.</summary>
    const uint LatencyMicroseconds = 100_000;

    IntPtr handle;

    internal AlsaPcmStream(int sampleRate, int channels, string? device, bool capture)
    {
        this.SampleRate = sampleRate;
        this.Channels = channels;

        // "default" routes through the plug plugin, which converts format/rate/channels for us.
        var name = device ?? "default";
        var result = Alsa.PcmOpen(out this.handle, name, capture ? Alsa.StreamCapture : Alsa.StreamPlayback, 0);
        if (result < 0)
            throw new InvalidOperationException($"Failed to open ALSA device '{name}': {Alsa.StrError(result)}");

        result = Alsa.PcmSetParams(
            this.handle,
            Alsa.FormatS16Le,
            Alsa.AccessRwInterleaved,
            (uint)channels,
            (uint)sampleRate,
            softResample: 1,
            LatencyMicroseconds
        );

        if (result < 0)
        {
            Alsa.PcmClose(this.handle);
            this.handle = IntPtr.Zero;
            throw new InvalidOperationException($"Failed to configure ALSA device '{name}': {Alsa.StrError(result)}");
        }
    }

    internal override int Read(byte[] buffer, int count)
    {
        if (this.handle == IntPtr.Zero)
            return 0;

        var frames = (nuint)(count / this.BytesPerFrame);
        var read = Alsa.PcmReadInterleaved(this.handle, ref buffer[0], frames);

        if (read < 0)
        {
            // An overrun is routine under load; recover and let the caller ask again.
            if (Alsa.PcmRecover(this.handle, (int)read, silent: 1) < 0)
                throw new IOException($"ALSA capture read failed: {Alsa.StrError((int)read)}");

            return 0;
        }

        return (int)read * this.BytesPerFrame;
    }

    internal override void Write(byte[] buffer, int offset, int count)
    {
        if (this.handle == IntPtr.Zero)
            return;

        // writei accepts as much as fits in the ring buffer, so keep going until it's all in.
        var end = offset + count;
        while (offset < end)
        {
            var frames = (nuint)((end - offset) / this.BytesPerFrame);
            if (frames == 0)
                break;

            var written = Alsa.PcmWriteInterleaved(this.handle, ref buffer[offset], frames);
            if (written < 0)
            {
                if (Alsa.PcmRecover(this.handle, (int)written, silent: 1) < 0)
                    throw new IOException($"ALSA playback write failed: {Alsa.StrError((int)written)}");

                continue;
            }

            offset += (int)written * this.BytesPerFrame;
        }
    }

    internal override void Drain()
    {
        if (this.handle != IntPtr.Zero)
            Alsa.PcmDrain(this.handle);
    }

    public override void Dispose()
    {
        if (this.handle == IntPtr.Zero)
            return;

        Alsa.PcmDrop(this.handle);
        Alsa.PcmClose(this.handle);
        this.handle = IntPtr.Zero;
    }
}
