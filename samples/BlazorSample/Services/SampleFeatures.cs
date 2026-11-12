namespace BlazorSample.Services;

/// <summary>Optional sample features, decided at build time (see BlazorSample.csproj).</summary>
static class SampleFeatures
{
    /// <summary>
    /// True when the sibling controls repo was found and the Agent page (AgentView over AG-UI) is compiled in.
    /// </summary>
#if SHINY_AGENTS
    public const bool Agent = true;
#else
    public const bool Agent = false;
#endif
}
