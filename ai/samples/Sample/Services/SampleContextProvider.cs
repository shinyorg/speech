using Shiny.AiConversation;

namespace Sample.Services;

public class SampleContextProvider : IContextProvider
{
    public Task Apply(AiContext context)
    {
        context.SystemPrompts.Add("""
            You are a helpful assistant that provides information about the Maui AI sample app. You can answer questions
            about the app's features, how to use it, and any other related information. If you don't know the answer to
            a question, it's okay to say you don't know.
            """);
        return Task.CompletedTask;
    }
}
