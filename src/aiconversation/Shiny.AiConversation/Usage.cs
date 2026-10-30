namespace Shiny.AiConversation;

/// <summary>
/// Everything billed in a session, cumulative across every model request (including each tool-loop round trip).
/// </summary>
public record SessionUsage(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long ReasoningTokens,
    long TotalTokens,
    int RequestCount
)
{
    public static SessionUsage Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Usage for one assistant message (summed over the requests of its turn). Null fields were not reported.
/// </summary>
public record UsageSnapshot(
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? ReasoningTokens,
    long? TotalTokens
);

/// <summary>
/// How full the context window is right now.
/// </summary>
/// <param name="CurrentTokens">Input + output of the last single model request, or an estimate.</param>
/// <param name="MaxTokens">The context window; null when unknown.</param>
/// <param name="PercentUsed">0..1 (can exceed 1); null when <paramref name="MaxTokens"/> is unknown.</param>
/// <param name="Pressure">Null when <paramref name="MaxTokens"/> is unknown.</param>
/// <param name="IsEstimate">True when the provider reported no usage and the size was estimated.</param>
public record ContextSnapshot(
    long CurrentTokens,
    long? MaxTokens,
    double? PercentUsed,
    ContextPressure? Pressure,
    bool IsEstimate,
    DateTimeOffset MeasuredAt
)
{
    public static ContextSnapshot Empty { get; } = new(0, null, null, null, true, DateTimeOffset.MinValue);
}

public enum ContextPressure
{
    Normal,
    Warning,
    Critical
}
