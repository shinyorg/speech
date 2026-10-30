namespace Shiny.AiConversation;

/// <summary>
/// Per-session settings, persisted in the session header so a session can be rehydrated exactly.
/// Runtime pieces (tools, prompts, chat client) are never persisted; they come from
/// <see cref="ISessionInterceptor"/>s, which can use <see cref="Profile"/> to decide what applies.
/// </summary>
public record AiSessionSettings
{
    /// <summary>
    /// Selects tools and prompts via interceptors (e.g. "cooking" vs "support").
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>
    /// The model's context window in tokens. Falls back to <see cref="IChatClientProvider.ContextWindow"/>.
    /// </summary>
    public int? ContextWindow { get; init; }

    public double ContextWarningThreshold { get; init; } = 0.75;
    public double ContextCriticalThreshold { get; init; } = 0.90;
    public CompactionSettings Compaction { get; init; } = new();

    /// <summary>
    /// Keeps this session out of conversation search.
    /// </summary>
    public bool ExcludeFromSearch { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public record CompactionSettings
{
    /// <summary>
    /// Safety net: compact automatically when the context is this full. Null disables it.
    /// </summary>
    public double? AutoCompactThreshold { get; init; } = 0.95;

    public int KeepRecentMessages { get; init; } = 10;

    /// <summary>
    /// Hard cap on prompt messages for when the context window is unknown.
    /// </summary>
    public int? MaxMessages { get; init; }

    public bool FlushToMemory { get; init; } = true;
}
