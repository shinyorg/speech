namespace Shiny.Audio;

/// <summary>
/// Platform-specific audio playback.
/// </summary>
public interface IAudioPlayer : IAsyncDisposable
{
    /// <summary>
    /// Play an audio stream (e.g. MP3). Completes when playback finishes or is cancelled.
    /// </summary>
    Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Play audio from a remote URL (<c>http</c>/<c>https</c>) or a local file path. The platform
    /// determines how to load the source — you never need to build a platform-specific file URI.
    /// Completes when playback finishes or is cancelled.
    /// </summary>
    /// <param name="source">An absolute <c>http</c>/<c>https</c> URL, or a local file system path.</param>
    Task PlayAsync(string source, CancellationToken cancellationToken = default)
        => PlaybackSource.PlayResolvedAsync(this, source, cancellationToken);

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
