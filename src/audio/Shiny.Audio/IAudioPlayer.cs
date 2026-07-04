namespace Shiny.Audio;

/// <summary>
/// Platform-specific audio playback for synthesized speech.
/// </summary>
public interface IAudioPlayer : IAsyncDisposable
{
    /// <summary>
    /// Play an audio stream (MP3 format). Completes when playback finishes or is cancelled.
    /// </summary>
    Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop any current playback.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Whether audio is currently playing.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    /// True when the platform can emit <see cref="AudioLevelChanged"/> samples during playback.
    /// </summary>
    bool IsPlayerAnalysisSupported { get; }

    /// <summary>
    /// Fires periodically during playback with the current output level normalized to 0.0 - 1.0.
    /// Only fires on platforms where <see cref="IsPlayerAnalysisSupported"/> is true.
    /// </summary>
    event EventHandler<double>? AudioLevelChanged;
}
