using Shiny.AiConversation;

namespace MauiSample.Services;

public class SampleInterceptor : ISessionInterceptor
{
    public Task OnSessionLoaded(SessionLoadedContext context)
    {
        context.SystemPrompts.Add("""
            You are K.I.T.T., a helpful assistant inside the Shiny Speech sample app. You can answer questions about
            the app's features and anything else. If you don't know the answer to a question, say so.
            """);
        return Task.CompletedTask;
    }
}
