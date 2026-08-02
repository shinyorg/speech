namespace Shiny.Audio;

/// <summary>Which version of the signal a recording captures.</summary>
public enum AudioRecordMode
{
    /// <summary>Write the processed signal — what the effect chain produced. The default.</summary>
    Wet,

    /// <summary>Write the untouched microphone signal, ignoring any effect chain.</summary>
    Dry,

    /// <summary>
    /// Write both, to two files, so the takes can be compared — or so the clean one can be
    /// re-rendered later with different settings via
    /// <see cref="AudioEffectProcessor.ProcessFile(string, string, AudioEffectChain)"/>.
    /// </summary>
    Both
}

/// <summary>
/// Records the microphone to a WAV file, optionally through a live <see cref="AudioEffectChain"/>.
/// </summary>
/// <remarks>
/// This owns the capture session and its drain loop, so a caller does not have to pump the PCM
/// stream from <see cref="IAudioSource"/> by hand. The effect chain is applied here rather than in
/// the source, which is what lets <see cref="AudioRecordMode.Both"/> write the before and after of
/// the same take.
/// </remarks>
public interface IAudioRecorder : IAsyncDisposable
{
    /// <summary>Request the runtime microphone permission required to record.</summary>
    Task<AccessState> RequestAccess();

    /// <summary>Begin recording. Throws if a recording is already running.</summary>
    Task StartAsync(AudioRecordingOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop and finalize the file(s). Returns details of what was written, or <c>null</c> if
    /// nothing was recorded.
    /// </summary>
    Task<AudioRecording?> StopAsync();

    /// <summary>True while a recording is in progress.</summary>
    bool IsRecording { get; }

    /// <summary>How much audio has been written so far.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>
    /// Fires periodically while recording with the level normalized 0.0 – 1.0, measured on the
    /// signal being written — so a meter matches the recorded file, effects included.
    /// </summary>
    /// <remarks>Raised on the drain thread — marshal to the UI thread before binding.</remarks>
    event EventHandler<double>? InputLevelChanged;
}

/// <summary>Options for <see cref="IAudioRecorder.StartAsync"/>.</summary>
public record AudioRecordingOptions
{
    /// <summary>
    /// Destination WAV file. When <c>null</c>, a timestamped file is created under the app's local
    /// data directory (<c>shiny.audio/recordings</c>). Parent directories are created as needed.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>Which signal to write. Default <see cref="AudioRecordMode.Wet"/>.</summary>
    public AudioRecordMode Mode { get; init; } = AudioRecordMode.Wet;

    /// <summary>
    /// Effects to apply. Keep the reference — toggling effects and moving parameters applies to the
    /// rest of the recording, live. Ignored when <see cref="Mode"/> is <see cref="AudioRecordMode.Dry"/>.
    /// </summary>
    public AudioEffectChain? Effects { get; init; }

    /// <summary>Platform voice processing (echo cancellation, noise suppression, AGC) for the capture session.</summary>
    public AudioProcessingOptions? Processing { get; init; }

    /// <summary>Stop automatically after this long. <c>null</c> records until stopped.</summary>
    public TimeSpan? MaxDuration { get; init; }
}

/// <summary>The result of a completed recording.</summary>
/// <param name="Path">
/// The file that was written — the processed take unless <see cref="AudioRecordMode.Dry"/> was used.
/// </param>
/// <param name="DryPath">
/// The unprocessed take, present only for <see cref="AudioRecordMode.Both"/>.
/// </param>
/// <param name="Duration">Length of the recording.</param>
/// <param name="SampleRate">Sample rate of the file — 16000, matching capture.</param>
/// <param name="Channels">Channel count — 1, matching capture.</param>
/// <param name="SizeInBytes">Size of <paramref name="Path"/> on disk.</param>
public record AudioRecording(
    string Path,
    string? DryPath,
    TimeSpan Duration,
    int SampleRate,
    int Channels,
    long SizeInBytes
);
