using AGUI.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AiConversation;
using Shiny.AiConversation.AgUi;

namespace Shiny;

public static class AgUiRegistrationExtensions
{
    /// <summary>
    /// Gives every session the <c>render_ui</c> tool, so the model can draw A2UI surfaces. Pair it with
    /// <see cref="AiSessionAgUiExtensions.RunAgUiAsync"/> and an A2UI renderer such as the Shiny AgentView.
    /// </summary>
    public static AiConversationOptions AddAgUiRenderTool(
        this AiConversationOptions options,
        string catalogId = AgUiRenderTool.ShinyCatalogId,
        string? catalogInstructions = null,
        params string[] profiles)
        => options.AddTool(new AgUiRenderTool(catalogId, catalogInstructions), AiToolFlags.Silent, profiles);

    /// <summary>
    /// Uses a remote AG-UI agent as the sessions' chat client (the official SDK's <c>AGUIChatClient</c>
    /// over HTTP + SSE). The agent runs its own tools; the session still keeps history, memory and
    /// compaction.
    /// </summary>
    /// <param name="endpoint">The agent's AG-UI endpoint.</param>
    /// <param name="httpClient">The client to post with; a new one when omitted.</param>
    public static AiConversationOptions AddAgUiChatClient(this AiConversationOptions options, Uri endpoint, HttpClient? httpClient = null)
    {
        options.Services.TryAddSingleton<IChatClientProvider>(_ => new AgUiChatClientProvider(endpoint, httpClient ?? new HttpClient()));
        return options;
    }
}

sealed class AgUiChatClientProvider(Uri endpoint, HttpClient http) : IChatClientProvider
{
    readonly IChatClient client = new AGUIChatClient(new AGUIChatClientOptions(http, endpoint.ToString()));

    public Task<IChatClient> GetChatClient(CancellationToken cancelToken = default) => Task.FromResult(client);
}
