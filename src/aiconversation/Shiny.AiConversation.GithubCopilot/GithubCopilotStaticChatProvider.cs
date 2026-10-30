using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OpenAI;
using Shiny.AiConversation;

namespace Shiny.AiConversation.GithubCopilot;

public class GithubCopilotStaticChatProvider(
    IServiceProvider services,
    string personalAccessToken,
    string modelName
) : IChatClientProvider
{
    const string CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token";
    const string CopilotBaseUrl = "https://api.githubcopilot.com";

    static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders =
        {
            { "Accept", "application/json" },
            { "User-Agent", "GitHubCopilotChat/0.26.7" }
        }
    };

    string? cachedCopilotToken;
    DateTimeOffset copilotTokenExpiry = DateTimeOffset.MinValue;
    IChatClient? cachedClient;

    public async Task<IChatClient> GetChatClient(CancellationToken cancelToken = default)
    {
        if (this.cachedClient != null && DateTimeOffset.UtcNow.AddSeconds(60) < this.copilotTokenExpiry)
            return this.cachedClient;

        var copilotToken = await this.GetCopilotToken(cancelToken).ConfigureAwait(false);

        var options = new OpenAIClientOptions { Endpoint = new Uri(CopilotBaseUrl) };
        options.AddPolicy(new CopilotHeadersPolicy(), PipelinePosition.PerCall);

        this.cachedClient = new ChatClientBuilder(
            new OpenAIClient(new ApiKeyCredential(copilotToken), options)
                .GetChatClient(modelName)
                .AsIChatClient()
        )
        .UseFunctionInvocation()
        .Build(services);

        return this.cachedClient;
    }

    async Task<string> GetCopilotToken(CancellationToken ct)
    {
        if (this.cachedCopilotToken != null && DateTimeOffset.UtcNow.AddSeconds(60) < this.copilotTokenExpiry)
            return this.cachedCopilotToken;

        using var request = new HttpRequestMessage(HttpMethod.Get, CopilotTokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", personalAccessToken);

        var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CopilotTokenResponse>(ct).ConfigureAwait(false);

        this.cachedCopilotToken = result!.Token;
        this.copilotTokenExpiry = DateTimeOffset.FromUnixTimeSeconds(result.ExpiresAt);
        this.cachedClient = null;

        return this.cachedCopilotToken;
    }
}

file record CopilotTokenResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expires_at")] long ExpiresAt
);

file class CopilotHeadersPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AddHeaders(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AddHeaders(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    static void AddHeaders(PipelineMessage message)
    {
        message.Request.Headers.Set("Editor-Version", "vscode/1.96.2");
        message.Request.Headers.Set("Editor-Plugin-Version", "copilot-chat/0.26.7");
        message.Request.Headers.Set("Copilot-Integration-Id", "vscode-chat");
        message.Request.Headers.Set("Openai-Intent", "conversation-panel");
    }
}
