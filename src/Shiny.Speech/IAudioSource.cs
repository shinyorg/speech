namespace Shiny.Speech;

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
    Task<Stream> StartCaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop capturing audio.
    /// </summary>
    Task StopCaptureAsync();
}
