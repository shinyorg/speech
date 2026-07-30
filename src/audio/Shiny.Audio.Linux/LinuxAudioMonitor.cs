using Microsoft.Extensions.Logging;
using Shiny.Audio.Interop;

namespace Shiny.Audio;

/// <summary>
/// Live mic-to-speaker monitoring on Linux: a capture stream is pumped straight into a playback
/// stream with gain applied.
/// </summary>
/// <remarks>
/// Apple and Android wire this up inside the platform audio graph; neither PulseAudio's simple API
/// nor ALSA offers an equivalent, so the loop lives here. Latency is therefore the sum of both
/// stream buffers — fine for a talkback/megaphone use case, not for live performance monitoring.
/// </remarks>
public class LinuxAudioMonitor(ILogger<LinuxAudioMonitor> logger) : IAudioMonitor
{
    const int SampleRate = 16000;
    const int ChannelCount = 1;
    const int ChunkBytes = SampleRate / 50 * 2 * ChannelCount;   // 20ms

    readonly Lock sync = new();

    PcmStream? capture;
    PcmStream? playback;
    Thread? pumpThread;
    CancellationTokenSource? cts;

    string? inputDevice;
    string? outputDevice;
    AudioProcessingOptions? processing;

    public bool IsMonitoring { get; private set; }
    public double Gain { get; set; } = 1.0;

    public event EventHandler<double>? InputLevelChanged;

    public Task<AccessState> RequestAccess() => Task.FromResult(
        PcmStream.Backend == LinuxAudioBackend.None ? AccessState.NotSupported : AccessState.Available
    );

    public Task Start(AudioMonitorOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (this.IsMonitoring)
            throw new InvalidOperationException("Monitoring is already running. Call Stop() first.");

        options ??= new AudioMonitorOptions();
        this.Gain = options.Gain;
        this.processing = options.Processing;
        this.inputDevice = options.InputDevice?.Id ?? this.inputDevice;
        this.outputDevice = options.OutputDevice?.Id ?? this.outputDevice;

        this.OpenStreams();

        this.cts = new CancellationTokenSource();
        var token = this.cts.Token;

        this.pumpThread = new Thread(() => this.Pump(token))
        {
            IsBackground = true,
            Name = "Shiny.Audio Linux monitor"
        };

        this.IsMonitoring = true;
        this.pumpThread.Start();

        logger.LogDebug("Linux audio monitoring started on {Backend}", PcmStream.Backend);
        return Task.CompletedTask;
    }

    void OpenStreams()
    {
        lock (this.sync)
        {
            this.capture = PcmStream.OpenCapture(SampleRate, ChannelCount, this.ResolveInputDevice());
            this.playback = PcmStream.OpenPlayback(SampleRate, ChannelCount, this.outputDevice);
        }
    }

    /// <summary>
    /// Echo cancellation matters more here than anywhere else — the mic is feeding the speaker it
    /// can hear. PulseAudio publishes cancellation as a virtual source from <c>module-echo-cancel</c>,
    /// so an explicitly chosen device wins, then that virtual source, then the default.
    /// </summary>
    string? ResolveInputDevice()
    {
        if (this.inputDevice != null)
            return this.inputDevice;

        if (this.processing?.EchoCancellation != true || PcmStream.Backend != LinuxAudioBackend.PulseAudio)
            return null;

        return PulseIntrospect
            .GetDevices(out _, out _)
            .FirstOrDefault(x => x.IsInput && x.Name.Contains("echo-cancel", StringComparison.OrdinalIgnoreCase))
            ?.Name;
    }

    void Pump(CancellationToken token)
    {
        var buffer = new byte[ChunkBytes];
        var throttle = new AudioLevelThrottle();

        try
        {
            while (!token.IsCancellationRequested)
            {
                PcmStream? source, sink;
                lock (this.sync)
                {
                    source = this.capture;
                    sink = this.playback;
                }

                if (source == null || sink == null)
                    break;

                var read = source.Read(buffer, buffer.Length);
                if (read <= 0)
                    continue;

                if (throttle.TryEmit(AudioLevel.FromPcm16(buffer.AsSpan(0, read)), out var level))
                    this.InputLevelChanged?.Invoke(this, level);

                ApplyGain(buffer, read, this.Gain);
                sink.Write(buffer, 0, read);
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            logger.LogError(ex, "Linux audio monitoring failed");
        }
    }

    /// <summary>Scale S16LE samples in place, saturating rather than wrapping on overflow.</summary>
    static void ApplyGain(byte[] buffer, int count, double gain)
    {
        if (Math.Abs(gain - 1.0) < 0.001)
            return;

        var samples = buffer.AsSpan(0, count - (count % 2));
        for (var i = 0; i + 1 < samples.Length; i += 2)
        {
            var sample = (short)(samples[i] | (samples[i + 1] << 8));
            var scaled = (short)Math.Clamp(sample * gain, short.MinValue, short.MaxValue);
            samples[i] = (byte)(scaled & 0xFF);
            samples[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }

    public async Task Stop()
    {
        if (this.cts == null)
            return;

        await this.cts.CancelAsync();

        // Disposing the streams unblocks the pump out of its native read/write.
        lock (this.sync)
        {
            this.capture?.Dispose();
            this.playback?.Dispose();
            this.capture = null;
            this.playback = null;
        }

        if (this.pumpThread != null)
        {
            this.pumpThread.Join(TimeSpan.FromSeconds(2));
            this.pumpThread = null;
        }

        this.cts.Dispose();
        this.cts = null;
        this.IsMonitoring = false;

        logger.LogDebug("Linux audio monitoring stopped");
    }

    public Task SetInputDevice(AudioDevice? device)
    {
        this.inputDevice = device?.Id;
        return this.Restart();
    }

    public Task SetOutputDevice(AudioDevice? device)
    {
        this.outputDevice = device?.Id;
        return this.Restart();
    }

    /// <summary>
    /// Both backends bind the device at stream-open time, so a live device change means tearing the
    /// pump down and bringing it back up. When idle the choice is simply remembered for the next
    /// <see cref="Start"/>.
    /// </summary>
    async Task Restart()
    {
        if (!this.IsMonitoring)
            return;

        var options = new AudioMonitorOptions { Gain = this.Gain, Processing = this.processing };
        await this.Stop();
        await this.Start(options);
    }

    public async ValueTask DisposeAsync()
    {
        await this.Stop();
        GC.SuppressFinalize(this);
    }
}
