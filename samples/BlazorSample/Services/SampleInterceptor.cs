using Shiny.AiConversation;

namespace BlazorSample.Services;

public class SampleInterceptor : ISessionInterceptor
{
    public Task OnSessionLoaded(SessionLoadedContext context)
    {
        context.SystemPrompts.Add("""
            You are a helpful assistant inside the Shiny Speech Blazor sample. You can answer questions about the
            sample's features and anything else. If you don't know the answer to a question, say so.
            """);
        return Task.CompletedTask;
    }
}
