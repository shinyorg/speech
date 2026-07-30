using Microsoft.Extensions.Logging;
using Shiny.Audio.Interop;

namespace Shiny.Audio;

/// <summary>
/// Microphone capture on Linux over PulseAudio/PipeWire, falling back to ALSA.
/// </summary>
/// <remarks>
/// The stream is opened at exactly the 16kHz/16-bit/mono the <see cref="IAudioSource"/> contract
/// promises; both backends resample from whatever the hardware actually runs at, so no conversion
/// happens in managed code.
/// </remarks>
public class LinuxAudioSource(ILogger<LinuxAudioSource> logger) : IAudioSource
{
    const int SampleRate = 16000;
    const int ChannelCount = 1;

    /// <summary>20ms of audio — small enough to keep the meter responsive, large enough to keep syscalls cheap.</summary>
    const int ChunkBytes = SampleRate / 50 * 2 * ChannelCount;

    PcmStream? stream;
    PipeStream? pipe;
    Thread? captureThread;
    CancellationTokenSource? cts;

    public event EventHandler<double>? InputLevelChanged;

    /// <summary>Linux has no runtime microphone permission gate — access is a filesystem/group concern.</summary>
    public Task<AccessState> RequestAccess() => Task.FromResult(
        PcmStream.Backend == LinuxAudioBackend.None ? AccessState.NotSupported : AccessState.Available
    );

    public Task<Stream> StartCaptureAsync(AudioProcessingOptions? processing = null, CancellationToken cancellationToken = default)
    {
        if (this.stream != null)
            throw new InvalidOperationException("Capture is already running. Call StopCaptureAsync() first.");

        var device = ResolveCaptureDevice(processing);
        this.stream = PcmStream.OpenCapture(SampleRate, ChannelCount, device);
        this.pipe = new PipeStream();

        // Linked so cancelling the caller's token ends capture, as well as StopCaptureAsync().
        this.cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var token = this.cts.Token;
        var pcm = this.stream;
        var sink = this.pipe;

        // A dedicated thread, not the thread pool: both backends block in native code for the whole
        // capture session, which would pin a pool thread for the duration.
        this.captureThread = new Thread(() => this.Pump(pcm, sink, token))
        {
            IsBackground = true,
            Name = "Shiny.Audio Linux capture"
        };
        this.captureThread.Start();

        logger.LogDebug("Linux audio capture started on {Backend} (device: {Device})", PcmStream.Backend, device ?? "default");
        return Task.FromResult<Stream>(this.pipe);
    }

    void Pump(PcmStream pcm, PipeStream sink, CancellationToken token)
    {
        var buffer = new byte[ChunkBytes];
        var throttle = new AudioLevelThrottle();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = pcm.Read(buffer, buffer.Length);
                if (read <= 0)
                    continue;   // an xrun the backend already recovered from

                if (throttle.TryEmit(AudioLevel.FromPcm16(buffer.AsSpan(0, read)), out var level))
                    this.InputLevelChanged?.Invoke(this, level);

                sink.Write(buffer, 0, read);
            }
        }
        catch (ObjectDisposedException)
        {
            // Stop() disposed the pipe out from under us — expected.
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            logger.LogError(ex, "Linux audio capture failed");
        }
    }

    /// <summary>
    /// PulseAudio exposes voice processing as a separate virtual source published by
    /// <c>module-echo-cancel</c>, so honouring the request means selecting that source when the
    /// user has loaded the module. Where it isn't loaded we capture raw, matching the best-effort
    /// contract on <see cref="AudioProcessingOptions"/>.
    /// </summary>
    static string? ResolveCaptureDevice(AudioProcessingOptions? processing)
    {
        if (processing?.AnyEnabled != true || PcmStream.Backend != LinuxAudioBackend.PulseAudio)
            return null;

        var devices = PulseIntrospect.GetDevices(out _, out _);
        return devices
            .FirstOrDefault(x => x.IsInput && x.Name.Contains("echo-cancel", StringComparison.OrdinalIgnoreCase))
            ?.Name;
    }

    public async Task StopCaptureAsync()
    {
        if (this.cts == null)
            return;

        await this.cts.CancelAsync();

        // Dispose the backend stream first: it unblocks the pump thread out of its native read.
        this.stream?.Dispose();
        this.stream = null;

        if (this.captureThread != null)
        {
            this.captureThread.Join(TimeSpan.FromSeconds(2));
            this.captureThread = null;
        }

        this.pipe?.Dispose();
        this.pipe = null;

        this.cts.Dispose();
        this.cts = null;

        logger.LogDebug("Linux audio capture stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopCaptureAsync();
        GC.SuppressFinalize(this);
    }
}
