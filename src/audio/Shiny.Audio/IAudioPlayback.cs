namespace Shiny.Audio;

/// <summary>
/// A single clip started by <see cref="IAudioPlayer.StartAsync(Stream, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// A player can have any number of these in flight at once, and each one is stopped on its own —
/// so a sound effect can be cancelled without touching the music underneath it.
/// <see cref="IAudioPlayer.StopAsync"/> stops all of them.
/// </remarks>
public interface IAudioPlayback : IAsyncDisposable
{
    /// <summary>
    /// Identifies this playback for the lifetime of the player. Useful for matching an entry in
    /// <see cref="IAudioPlayer.Active"/> back to whatever started it.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// The URL or file path this playback was started from, or <c>null</c> when it was started from
    /// a <see cref="Stream"/>.
    /// </summary>
    string? Source { get; }

    /// <summary>
    /// True until the clip ends, is stopped, or is cancelled.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Completes when the clip ends, is stopped, or its cancellation token fires — cancellation is a
    /// normal completion, not an <see cref="OperationCanceledException"/>. Faults only when the
    /// platform reports a playback error.
    /// </summary>
    Task Completion { get; }

    /// <summary>
    /// Stop this playback and release its native resources, leaving every other playback alone.
    /// Safe to call more than once; the returned task completes once teardown has finished.
    /// </summary>
    Task StopAsync();
}
