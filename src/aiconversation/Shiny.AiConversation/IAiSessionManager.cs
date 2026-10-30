namespace Shiny.AiConversation;

/// <summary>
/// Creates, loads and removes AI sessions. The manager is stateless: it never caches session instances,
/// so every <see cref="Get"/> / <see cref="GetLatest"/> reads the store and returns a new instance that the
/// caller owns and disposes.
/// </summary>
public interface IAiSessionManager
{
    /// <summary>
    /// The assistant's memory documents, for showing, editing and deleting what it remembers.
    /// </summary>
    IAiMemoryService Memory { get; }

    /// <summary>
    /// Persists a new session and returns an instance of it.
    /// </summary>
    Task<IAiSession> Start(AiSessionSettings? settings = null, CancellationToken ct = default);

    /// <summary>
    /// Loads a session from the store. Returns null when it doesn't exist.
    /// </summary>
    Task<IAiSession?> Get(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Loads the most recently active session ("continue where I left off"). Returns null when there are none.
    /// </summary>
    Task<IAiSession?> GetLatest(CancellationToken ct = default);

    /// <summary>
    /// Queries session headers without loading them, most recently active first.
    /// </summary>
    Task<IReadOnlyList<AiSessionDocument>> Query(SessionQuery query, CancellationToken ct = default);

    /// <summary>
    /// Deletes a session, all of its messages and its session-scoped memory.
    /// </summary>
    Task Delete(string sessionId, CancellationToken ct = default);
}
