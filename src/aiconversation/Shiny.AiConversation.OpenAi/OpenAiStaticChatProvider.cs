using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Shiny.AiConversation.OpenAi;

public class OpenAiStaticChatProvider : IChatClientProvider
{
    readonly IChatClient chatClient;
    
    
    public OpenAiStaticChatProvider(
        IServiceProvider services, 
        string apiToken, 
        string endpointUri, 
        string modelName,
        Action<ChatClientBuilder>? action = null
    )
    {
        var openAiClient = new OpenAIClient(
            new ApiKeyCredential(apiToken),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(endpointUri)
            }
        );
        var builder = new ChatClientBuilder(openAiClient.GetChatClient(modelName).AsIChatClient());
        action?.Invoke(builder);
        
        this.chatClient = builder
            .UseFunctionInvocation()
            .Build(services);
    }
    
    
    public Task<IChatClient> GetChatClient(CancellationToken cancelToken = default)
        => Task.FromResult(this.chatClient);
}