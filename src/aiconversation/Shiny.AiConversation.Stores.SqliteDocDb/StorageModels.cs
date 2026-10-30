using System.Text.Json.Serialization;

namespace Shiny.AiConversation.Stores.SqliteDocDb;

// Storage wrappers. The core documents are immutable records without an "Id" property, so each is
// nested inside a small mutable document that carries the id, the version used for optimistic
// concurrency, and denormalized fields for querying and indexing.

public class StoredAiSession
{
    public string Id { get; set; } = null!;
    public int Version { get; set; }
    public long LastActivityTicks { get; set; }
    public string? Profile { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public AiSessionDocument Session { get; set; } = null!;
}

public class StoredAiMessage
{
    public string Id { get; set; } = null!;
    public string SessionId { get; set; } = null!;
    public long Sequence { get; set; }
    public bool Compacted { get; set; }

    /// <summary>
    /// User or assistant text: the only messages conversation search looks at.
    /// </summary>
    public bool Searchable { get; set; }

    public long TimestampTicks { get; set; }
    public string? Text { get; set; }
    public AiMessageDocument Message { get; set; } = null!;
}

/// <summary>
/// Per-session message sequence counter, versioned so concurrent appends never share a sequence.
/// </summary>
public class StoredAiSequence
{
    public string Id { get; set; } = null!;
    public int Version { get; set; }
    public long Next { get; set; }
}

public class StoredAiMemory
{
    public string Id { get; set; } = null!;
    public int Version { get; set; }
    public string Scope { get; set; } = null!;

    /// <summary>
    /// Empty (not null) for the global scope, so equality queries work.
    /// </summary>
    public string ScopeKey { get; set; } = "";

    public string Path { get; set; } = null!;
    public long UpdatedTicks { get; set; }
    public int Size { get; set; }
    public AiMemoryDocument Memory { get; set; } = null!;
}


[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(StoredAiSession))]
[JsonSerializable(typeof(StoredAiMessage))]
[JsonSerializable(typeof(StoredAiSequence))]
[JsonSerializable(typeof(StoredAiMemory))]
public partial class AiConversationStoreJsonContext : JsonSerializerContext;
