using System.Threading.Channels;

namespace Shiny.Audio.Duplex;

/// <summary>
/// The platform-agnostic half of an <see cref="IAudioDuplexSession"/>: frame chunking and the bounded
/// <see cref="Captured"/> channel on the way in, the <see cref="AudioJitterBuffer"/> on the way out, input
/// metering, and once-only stop. A platform backend feeds <see cref="OnCaptured"/> from its capture
/// thread, pulls <see cref="ReadPlayback"/> from its output, and tears its own graph down in
/// <see cref="StopCoreAsync"/>.
/// </summary>
abstract class AudioDuplexSession : IAudioDuplexSession
{
    /// <summary>How much captured audio the channel holds before the oldest frame is dropped.</summary>
    internal static readonly TimeSpan CapturedCapacity = TimeSpan.FromSeconds(1);

    readonly Channel<ReadOnlyMemory<byte>> captured;
    readonly AudioJitterBuffer jitter;
    readonly PcmFrameChunker chunker;
    readonly AudioLevelThrottle throttle = new();
    readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int stopState;
    AudioDevice? inputDevice;

    protected AudioDuplexSession(AudioDuplexOptions options)
    {
        this.Options = options;
        this.inputDevice = options.InputDevice;
        this.jitter = new AudioJitterBuffer(options.SampleRate, options.PlaybackLatency, options.MaxPlaybackLatency);
        this.chunker = new PcmFrameChunker(options.FrameBytes);

        var capacity = Math.Max(1, (int)(CapturedCapacity.TotalMilliseconds / options.FrameMilliseconds));
        this.captured = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });
    }

    protected AudioDuplexOptions Options { get; }

    /// <summary>Raised once, after the session has stopped (the duplex service frees its slot).</summary>
    internal event Action? Stopped;

    internal bool IsStopped => Volatile.Read(ref this.stopState) != 0;

    internal AudioJitterBuffer Jitter => this.jitter;

    public int SampleRate => this.Options.SampleRate;

    public ChannelReader<ReadOnlyMemory<byte>> Captured => this.captured.Reader;

    public TimeSpan Buffered => this.jitter.Buffered;

    public abstract bool Speakerphone { get; set; }

    public AudioDevice? InputDevice
    {
        get => Volatile.Read(ref this.inputDevice);
        set
        {
            if (value is { Io: not AudioDeviceIo.Input })
                throw new ArgumentException($"'{value.Name}' is an output device. Pick one from IAudioDevices.GetInputs().", nameof(value));

            // Same mic (by id: IsCurrent and the name can differ between enumerations) is a no-op.
            if (SameDevice(this.InputDevice, value))
                return;

            if (this.IsStopped)
            {
                Volatile.Write(ref this.inputDevice, value);
                return;
            }

            // A mic that isn't there any more falls back to the default, and the property says so.
            var applied = this.SelectInputDevice(value);
            Volatile.Write(ref this.inputDevice, applied ? value : null);
        }
    }

    public event EventHandler? InputDeviceChanged;

    public event EventHandler<double>? InputLevelChanged;

    /// <summary>
    /// Point the running graph at <paramref name="device"/> (null = the system default). Return false when
    /// the device isn't available, after falling back to the default. Not called once the session stops.
    /// </summary>
    protected abstract bool SelectInputDevice(AudioDevice? device);

    /// <summary>
    /// The chosen mic has gone away (or couldn't be applied at start) and the platform is back on the
    /// default: <see cref="InputDevice"/> becomes null and, when <paramref name="notify"/>,
    /// <see cref="InputDeviceChanged"/> fires. Call outside any platform lock.
    /// </summary>
    protected void OnInputDeviceUnavailable(bool notify = true)
    {
        if (Interlocked.Exchange(ref this.inputDevice, null) != null && notify && !this.IsStopped)
            this.InputDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static bool SameDevice(AudioDevice? a, AudioDevice? b)
        => a == null ? b == null : b != null && a.Id == b.Id;

    public void Play(ReadOnlySpan<byte> pcm16)
    {
        if (!this.IsStopped)
            this.jitter.Write(pcm16);
    }

    /// <summary>Feed processed PCM16 mono at <see cref="SampleRate"/>, any length. Call from one capture thread.</summary>
    protected void OnCaptured(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.IsEmpty || this.IsStopped)
            return;

        this.chunker.Push(pcm16, frame => this.captured.Writer.TryWrite(frame));

        if (this.InputLevelChanged != null && this.throttle.TryEmit(AudioLevel.FromPcm16(pcm16), out var level))
            this.InputLevelChanged.Invoke(this, level);
    }

    /// <summary>Fill an output buffer from the jitter buffer (silence where there's nothing to play).</summary>
    protected int ReadPlayback(Span<byte> destination) => this.jitter.Read(destination);

    public Task StopAsync()
    {
        // Once only: every caller (and a re-entrant one from inside StopCoreAsync) gets the same task.
        if (Interlocked.Exchange(ref this.stopState, 1) == 0)
            _ = this.StopInternalAsync();

        return this.stopped.Task;
    }

    async Task StopInternalAsync()
    {
        Exception? error = null;
        try
        {
            await this.StopCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        this.captured.Writer.TryComplete();
        this.jitter.Clear();
        this.Stopped?.Invoke();

        if (error == null)
            this.stopped.TrySetResult();
        else
            this.stopped.TrySetException(error);
    }

    /// <summary>Stop the platform graph and restore whatever audio-session state it changed.</summary>
    protected abstract Task StopCoreAsync();

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
