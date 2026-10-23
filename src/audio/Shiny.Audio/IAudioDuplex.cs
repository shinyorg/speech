using System.Threading.Channels;

namespace Shiny.Audio;

/// <summary>
/// Full-duplex voice: microphone capture and live, open-ended PCM playback on <b>one</b>
/// voice-processing session, so the platform's echo canceller removes what the speaker plays from
/// what the microphone hears. This is the audio half of a phone / FaceTime-style call: hand every
/// captured frame to the network, and every frame that arrives from the network to
/// <see cref="IAudioDuplexSession.Play"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why not <see cref="IAudioSource"/> plus <see cref="IAudioPlayer"/>? Two separate sessions can't
/// cancel each other's echo, and <see cref="IAudioPlayer.StartAsync(System.IO.Stream, System.Threading.CancellationToken)"/>
/// is built for clips (Apple buffers the whole stream, Android copies it to a file first), not for
/// frames that arrive every 20 ms and must play immediately.
/// </para>
/// <para>
/// One session at a time — it owns the device's call audio. Implemented on iOS, Mac Catalyst, macOS
/// and Android. Not registered on Windows, Linux or the browser.
/// </para>
/// </remarks>
public interface IAudioDuplex
{
    /// <summary>Request the runtime microphone permission a session needs.</summary>
    Task<AccessState> RequestAccess();

    /// <summary>
    /// Configure the platform for a voice call and start capturing and playing.
    /// </summary>
    /// <param name="options">Sample rate, processing, routing and jitter-buffer settings. Null uses the defaults.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running session. Stop (or dispose) it to restore the audio session.</returns>
    /// <exception cref="InvalidOperationException">A session is already running, or the platform couldn't start one.</exception>
    /// <exception cref="ArgumentException">The options are invalid (see <see cref="AudioDuplexOptions"/>).</exception>
    Task<IAudioDuplexSession> StartAsync(AudioDuplexOptions? options = null, CancellationToken cancellationToken = default);
}


/// <summary>
/// A running full-duplex voice session from <see cref="IAudioDuplex.StartAsync"/>. All audio is PCM16
/// mono little-endian at <see cref="SampleRate"/>, in both directions.
/// </summary>
public interface IAudioDuplexSession : IAsyncDisposable
{
    /// <summary>The PCM sample rate of <see cref="Captured"/> and <see cref="Play"/>: 8000 or 16000.</summary>
    int SampleRate { get; }

    /// <summary>
    /// The processed (echo-cancelled) microphone, one <see cref="AudioDuplexOptions.FrameDuration"/>
    /// frame per item, each a buffer you own. Bounded: a reader that falls about a second behind loses
    /// the oldest frames rather than growing the delay. Completes when the session stops.
    /// </summary>
    ChannelReader<ReadOnlyMemory<byte>> Captured { get; }

    /// <summary>
    /// Queue received PCM for playback. Thread-safe and non-blocking, any length (an odd trailing byte is
    /// ignored). Playback waits until about <see cref="AudioDuplexOptions.PlaybackLatency"/> is queued,
    /// plays silence through gaps, and drops the oldest audio once more than
    /// <see cref="AudioDuplexOptions.MaxPlaybackLatency"/> is queued. Ignored after the session stops.
    /// </summary>
    void Play(ReadOnlySpan<byte> pcm16);

    /// <summary>How much received audio is queued and not yet handed to the output.</summary>
    TimeSpan Buffered { get; }

    /// <summary>
    /// Play through the loudspeaker rather than the earpiece. Applies live. A connected headset
    /// (wired, or Bluetooth when <see cref="AudioProcessingOptions.AllowBluetooth"/>) takes priority
    /// either way. No effect on macOS, which has no earpiece.
    /// </summary>
    bool Speakerphone { get; set; }

    /// <summary>
    /// Fires about 20 times a second with the captured (processed) microphone level, 0.0 – 1.0.
    /// Raised on the capture thread.
    /// </summary>
    event EventHandler<double>? InputLevelChanged;

    /// <summary>
    /// Stop capture and playback, complete <see cref="Captured"/>, and restore the audio session
    /// category / mode / routing this session changed. Safe to call more than once.
    /// </summary>
    Task StopAsync();
}


/// <summary>Options for <see cref="IAudioDuplex.StartAsync"/>.</summary>
public record AudioDuplexOptions
{
    /// <summary>PCM sample rate in both directions: 8000 or 16000. Default 16000 (wideband voice).</summary>
    public int SampleRate { get; init; } = 16000;

    /// <summary>
    /// Voice processing. Default <see cref="AudioProcessingOptions.VoiceChat"/> (echo cancellation,
    /// noise suppression, AGC). Apple's voice-processing unit can't toggle the effects one by one, so
    /// any enabled flag turns on all three there. Null (or nothing enabled) means no processing — and no
    /// echo cancellation, so only do that with a headset.
    /// </summary>
    public AudioProcessingOptions? Processing { get; init; } = AudioProcessingOptions.VoiceChat;

    /// <summary>Start on the loudspeaker rather than the earpiece. Default true. Changeable live via <see cref="IAudioDuplexSession.Speakerphone"/>.</summary>
    public bool Speakerphone { get; init; } = true;

    /// <summary>Length of each <see cref="IAudioDuplexSession.Captured"/> frame: whole milliseconds, 10–100. Default 20 ms.</summary>
    public TimeSpan FrameDuration { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Target jitter buffer: playback doesn't start until about this much audio is queued, and after an
    /// underrun (the queue ran dry) it waits for this much again. Higher rides out more network jitter
    /// at the cost of delay. Default 60 ms. Zero plays as soon as anything arrives.
    /// </summary>
    public TimeSpan PlaybackLatency { get; init; } = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// The most audio that may queue. When a burst pushes the queue past this, the oldest audio is dropped
    /// back down to <see cref="PlaybackLatency"/>, so delay can't build up. Must be greater than
    /// <see cref="PlaybackLatency"/>. Default 200 ms.
    /// </summary>
    public TimeSpan MaxPlaybackLatency { get; init; } = TimeSpan.FromMilliseconds(200);

    internal int FrameMilliseconds => (int)this.FrameDuration.TotalMilliseconds;

    /// <summary>Bytes in one PCM16 mono frame.</summary>
    internal int FrameBytes => this.SampleRate / 1000 * this.FrameMilliseconds * 2;

    internal void Validate()
    {
        if (this.SampleRate is not (8000 or 16000))
            throw new ArgumentException($"SampleRate must be 8000 or 16000 (was {this.SampleRate}).", nameof(this.SampleRate));

        var ms = this.FrameDuration.TotalMilliseconds;
        if (ms < 10 || ms > 100 || ms != Math.Floor(ms))
            throw new ArgumentException($"FrameDuration must be whole milliseconds between 10 and 100 (was {ms} ms).", nameof(this.FrameDuration));

        if (this.PlaybackLatency < TimeSpan.Zero)
            throw new ArgumentException("PlaybackLatency can't be negative.", nameof(this.PlaybackLatency));

        if (this.MaxPlaybackLatency <= this.PlaybackLatency)
            throw new ArgumentException(
                $"MaxPlaybackLatency ({this.MaxPlaybackLatency.TotalMilliseconds} ms) must be greater than PlaybackLatency ({this.PlaybackLatency.TotalMilliseconds} ms).",
                nameof(this.MaxPlaybackLatency)
            );
    }
}
