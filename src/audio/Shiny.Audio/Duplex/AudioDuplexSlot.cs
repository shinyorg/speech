namespace Shiny.Audio.Duplex;

/// <summary>
/// Enforces <see cref="IAudioDuplex"/>'s one-session-at-a-time rule for a platform backend, validates
/// the options, and frees the slot when the session stops.
/// </summary>
sealed class AudioDuplexSlot
{
    readonly object gate = new();
    AudioDuplexSession? current;
    bool starting;

    /// <summary>The running session, if any.</summary>
    public IAudioDuplexSession? Current
    {
        get { lock (this.gate) return this.current; }
    }

    /// <summary>
    /// Validate <paramref name="options"/> (null = defaults), claim the slot, and start a session through
    /// <paramref name="start"/>. The slot is released if the start fails, and when the session stops.
    /// </summary>
    public async Task<IAudioDuplexSession> StartAsync(
        AudioDuplexOptions? options,
        CancellationToken cancellationToken,
        Func<AudioDuplexOptions, CancellationToken, Task<AudioDuplexSession>> start
    )
    {
        options ??= new AudioDuplexOptions();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.gate)
        {
            if (this.starting || this.current != null)
                throw new InvalidOperationException("An audio duplex session is already running. Stop it before starting another.");
            this.starting = true;
        }

        try
        {
            var session = await start(options, cancellationToken).ConfigureAwait(false);
            session.Stopped += () =>
            {
                lock (this.gate)
                {
                    if (this.current == session)
                        this.current = null;
                }
            };

            lock (this.gate)
            {
                // A session that stopped before we got here (its Stopped event already ran) never holds the slot.
                if (!session.IsStopped)
                    this.current = session;
            }
            return session;
        }
        finally
        {
            lock (this.gate)
                this.starting = false;
        }
    }
}
