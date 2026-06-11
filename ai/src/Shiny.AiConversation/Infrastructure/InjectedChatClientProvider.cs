using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.AiConversation.Infrastructure;

public class InjectedChatClientProvider(IServiceProvider services) : IChatClientProvider
{
    public Task<IChatClient> GetChatClient(CancellationToken cancelToken = default)
    {
        var chatClient = services.GetService<IChatClient>();
        if (chatClient == null)
            throw new InvalidOperationException($"You must have an IChatClient registered on your DI container OR you have to implement Shiny.AiConversation.IChatClientProvider");
        
        return Task.FromResult(chatClient);
    }
}