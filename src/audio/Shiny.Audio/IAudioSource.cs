namespace Shiny.Audio;

/// <summary>
/// Platform-specific audio capture source that provides raw PCM audio data.
/// </summary>
public interface IAudioSource : IAsyncDisposable
{
    /// <summary>
    /// Request the runtime microphone permission required for capture. Platforms
    /// that don't gate microphone access behind a runtime prompt return
    /// <see cref="AccessState.Available"/>.
    /// </summary>
    Task<AccessState> RequestAccess() => Task.FromResult(AccessState.Available);

    /// <summary>
    /// Start capturing audio from the microphone.
    /// Returns a stream of raw PCM audio data (16kHz, 16-bit, mono).
    /// </summary>
    /// <param name="processing">
    /// Optional platform voice-processing effects (echo cancellation, noise suppression,
    /// automatic gain control) to apply to the capture session. <c>null</c> captures raw
    /// input. Effects are best-effort and device-dependent.
    /// <para>
    /// If the captured audio feeds a model rather than a listener — speaker recognition, wake
    /// words — pass <see cref="AudioProcessingOptions.Analysis"/>: the effects above are adaptive
    /// and normalize away the speaker/channel characteristics such models measure, and a Bluetooth
    /// mic caps capture at 8 kHz narrowband.
    /// </para>
    /// </param>
    Task<Stream> StartCaptureAsync(AudioProcessingOptions? processing = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop capturing audio.
    /// </summary>
    Task StopCaptureAsync();

    /// <summary>
    /// Fires periodically while capturing with the microphone level normalized 0.0 – 1.0 — the
    /// "listening" counterpart to <see cref="IAudioPlayer.AudioLevelChanged"/>. Levels are computed
    /// from the captured PCM (see <see cref="AudioLevel"/>) and throttled to roughly 20 events per
    /// second, so they can drive a VU meter directly. Supported on every platform.
    /// </summary>
    /// <remarks>
    /// Raised on the platform's capture thread — marshal to the UI thread before binding.
    /// </remarks>
    event EventHandler<double>? InputLevelChanged;
}
