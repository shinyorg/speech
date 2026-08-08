namespace Shiny.Audio.Infrastructure;

/// <summary>
/// The <see cref="IAudioPlayback"/> the platform players hand back — owns the completion signal and
/// guarantees the native teardown runs exactly once.
/// </summary>
/// <remarks>Create these through <see cref="AudioPlaybackRegistry.Create"/>, never directly.</remarks>
public sealed class AudioPlayback : IAudioPlayback
{
    readonly TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly AudioPlaybackRegistry registry;
    readonly Lock sync = new();

    Func<Task>? teardown;
    CancellationTokenRegistration ctr;
    Task? finishing;

    internal AudioPlayback(AudioPlaybackRegistry registry, string? source)
    {
        this.registry = registry;
        this.Source = source;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string? Source { get; }
    public Task Completion => this.tcs.Task;
    public bool IsPlaying => !this.tcs.Task.IsCompleted;

    /// <summary>The level last reported for this clip. Read and written under the registry's lock.</summary>
    internal double Level { get; set; }

    /// <summary>
    /// Registers the native cleanup for this clip — stop and dispose the platform player, delete the
    /// temp file, and so on. It runs exactly once, and always off the native callback stack.
    /// </summary>
    public void OnStop(Func<Task> teardown)
    {
        lock (this.sync)
        {
            if (this.finishing == null)
            {
                this.teardown = teardown;
                return;
            }
        }

        // Already finished before the native resources were attached — a StopAsync/StopAllAsync that
        // raced the start. Run the teardown now rather than leaking the native player.
        _ = Task.Run(teardown);
    }

    /// <summary>
    /// Cancelling <paramref name="cancellationToken"/> stops this clip and nothing else. Call it
    /// after the clip has actually started so a token that is already cancelled tears down a
    /// fully-constructed playback.
    /// </summary>
    public void CancelWith(CancellationToken cancellationToken)
        => this.ctr = cancellationToken.Register(() => _ = this.StopAsync());

    /// <summary>The clip reached its natural end. Safe to call from a native completion callback.</summary>
    public void Complete() => _ = this.FinishAsync(null);

    /// <summary>
    /// The native player reported a failure. The exception surfaces on <see cref="Completion"/> (and
    /// therefore on <see cref="IAudioPlayer.PlayAsync(Stream, CancellationToken)"/>).
    /// </summary>
    public void Fail(Exception ex) => _ = this.FinishAsync(ex);

    public Task StopAsync() => this.FinishAsync(null);

    public ValueTask DisposeAsync() => new(this.StopAsync());

    Task FinishAsync(Exception? error)
    {
        lock (this.sync)
        {
            if (this.finishing != null)
                return this.finishing;

            this.ctr.Unregister();
            this.registry.Remove(this);

            // Task.Run is not incidental: Complete()/Fail() are raised from native completion
            // callbacks (AVAudioPlayer.FinishedPlaying, MediaPlayer.Completion, ...) and disposing
            // the native player on that stack corrupts the runtime. Hopping to the pool unwinds the
            // callback before teardown touches anything.
            this.finishing = Task.Run(async () =>
            {
                try
                {
                    var stop = this.teardown;
                    if (stop != null)
                        await stop().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    error ??= ex;
                }

                if (error == null)
                    this.tcs.TrySetResult();
                else
                    this.tcs.TrySetException(error);
            });
            return this.finishing;
        }
    }
}
