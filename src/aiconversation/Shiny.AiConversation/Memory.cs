namespace Shiny.AiConversation;

/// <summary>
/// Where a memory document lives.
/// </summary>
public enum MemoryScope
{
    /// <summary>
    /// Scratchpad for one conversation; deleted with the session. Key: the session id.
    /// </summary>
    Session,

    /// <summary>
    /// Shared by sessions with the same <see cref="AiSessionSettings.Profile"/>. Key: the profile name.
    /// </summary>
    Profile,

    /// <summary>
    /// Known to every session (facts about the user). No key.
    /// </summary>
    Global
}

public enum MemoryAuthor
{
    Assistant,
    User,
    App
}

public enum MemoryChange
{
    Created,
    Updated,
    Deleted,
    Renamed
}

/// <summary>
/// A note the assistant keeps for itself. Never spoken or shown in the chat; the user can see, edit and
/// delete every document through <see cref="IAiMemoryService"/>.
/// </summary>
/// <param name="Path">e.g. "/notes/preferences.md".</param>
/// <param name="ScopeKey">The session id for <see cref="MemoryScope.Session"/>, the profile name for <see cref="MemoryScope.Profile"/>, null for <see cref="MemoryScope.Global"/>.</param>
/// <param name="Description">One line, shown in the prompt index.</param>
/// <param name="Version">Optimistic concurrency token, bumped on every save.</param>
public record AiMemoryDocument(
    string Path,
    MemoryScope Scope,
    string? ScopeKey,
    string? Description,
    string Content,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    MemoryAuthor UpdatedBy,
    long Version,
    int SchemaVersion
)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Persists memory documents. Separate from <see cref="IAiSessionStore"/> (different query patterns),
/// though a store package may implement both.
/// </summary>
public interface IAiMemoryStore
{
    Task<AiMemoryDocument?> Get(MemoryScope scope, string? scopeKey, string path, CancellationToken ct = default);

    /// <summary>
    /// Documents ordered by most recently updated first.
    /// </summary>
    Task<IReadOnlyList<AiMemoryDocument>> List(MemoryScope? scope = null, string? scopeKey = null, string? pathPrefix = null, CancellationToken ct = default);

    /// <summary>
    /// Creates (<paramref name="expectedVersion"/> null; throws <see cref="MemoryConflictException"/> when the path exists)
    /// or replaces (stored version must equal <paramref name="expectedVersion"/>). The caller sets the new version.
    /// </summary>
    Task Save(AiMemoryDocument document, long? expectedVersion, CancellationToken ct = default);

    Task Delete(MemoryScope scope, string? scopeKey, string path, CancellationToken ct = default);

    /// <summary>
    /// Deletes every document in a scope (and key, when given).
    /// </summary>
    Task Clear(MemoryScope scope, string? scopeKey = null, CancellationToken ct = default);

    /// <summary>
    /// Total content size in bytes (UTF-8) of a scope + key.
    /// </summary>
    Task<long> GetScopeSize(MemoryScope scope, string? scopeKey, CancellationToken ct = default);
}

/// <summary>
/// App-facing memory API: the user must be able to see, edit and delete everything the assistant remembers.
/// Writes are marked <see cref="MemoryAuthor.User"/> so the model can be told the user edited a note.
/// </summary>
public interface IAiMemoryService
{
    Task<IReadOnlyList<AiMemoryDocument>> List(MemoryScope? scope = null, string? scopeKey = null, CancellationToken ct = default);
    Task<AiMemoryDocument?> Get(MemoryScope scope, string? scopeKey, string path, CancellationToken ct = default);
    Task<AiMemoryDocument> Save(MemoryScope scope, string? scopeKey, string path, string content, string? description = null, CancellationToken ct = default);
    Task Delete(MemoryScope scope, string? scopeKey, string path, CancellationToken ct = default);

    /// <summary>
    /// "Forget everything": clears one scope, or every scope when <paramref name="scope"/> is null.
    /// </summary>
    Task Clear(MemoryScope? scope = null, string? scopeKey = null, CancellationToken ct = default);
}

/// <summary>
/// Memory limits. They protect the model's context, not the disk, so they're soft: exceeding one returns an
/// error to the model (which then condenses or deletes) rather than failing the turn. Null disables a limit.
/// </summary>
public record AiMemoryOptions
{
    /// <summary>
    /// Turns the <c>memory</c> tool and prompt index off for every session.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Sessions with one of these profiles get no memory tool or index.
    /// </summary>
    public IReadOnlyList<string> DisabledProfiles { get; init; } = [];

    public int? MaxDocumentBytes { get; init; } = 16 * 1024;
    public int? MaxIndexEntries { get; init; } = 50;
    public int? MaxIndexTokens { get; init; } = 1500;
    public long? MaxScopeBytes { get; init; } = 256 * 1024;
    public int? MaxWritesPerTurn { get; init; } = 20;
}

/// <summary>
/// Thrown when a memory document was changed (or created) by someone else since it was read.
/// </summary>
public sealed class MemoryConflictException(string path) : Exception($"Memory document '{path}' was modified concurrently")
{
    public string Path => path;
}

/// <summary>
/// Summarizes older messages for compaction. Register a custom one with
/// <see cref="Shiny.AiConversation.Infrastructure.ChatClientContextCompactor"/> as the reference implementation.
/// </summary>
public interface IContextCompactor
{
    /// <summary>
    /// Folds <paramref name="previousSummary"/> and <paramref name="messages"/> into one new summary.
    /// </summary>
    Task<string> Summarize(string? previousSummary, IReadOnlyList<AiMessageDocument> messages, CancellationToken ct);
}
