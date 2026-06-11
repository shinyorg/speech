using Microsoft.Extensions.AI;
using Shiny.AiConversation.Infrastructure;
using Shiny.Speech;

namespace Shiny.AiConversation;

/// <summary>
/// This is the AI context that will be passed to the AI engine. It contains the tools and system prompts that the AI can
/// use to generate responses. The context providers will populate this context with the necessary information for the AI
/// to function properly.
/// </summary>
public class AiContext
{
    public static IReadOnlyList<string> DefaultQuietWords { get; } = ["cancel", "quiet", "shut up", "stop", "nevermind", "never mind", "hush"];

    public AiAcknowledgement Acknowledgement { get; set; }
    public List<AITool> Tools { get; } = [];
    public List<string> SystemPrompts { get; } = [];
    public List<string>? QuietWords { get; } = [..DefaultQuietWords];
    
    /// <summary>
    /// Options passed to the speech-to-text session that runs for the duration of wake-word /
    /// push-to-talk activity. Keywords specified here are merged with the wake word at session start.
    /// </summary>
    public SpeechRecognitionOptions? SpeechToTextOptions { get; set; }

    /// <summary>
    /// Options passed to text-to-speech when speaking AI responses aloud.
    /// </summary>
    public Shiny.Speech.TextToSpeechOptions? TextToSpeechOptions { get; set; }
}

public interface IContextProvider
{
    
    /// <summary>
    /// Applies the context provider to the given AI context. This method is responsible for populating the context with the necessary tools and system prompts that the AI can use to generate responses.
    /// Context providers are executed in sequence, allowing them to build upon each other's contributions to the context.
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    Task Apply(AiContext context);
}


public sealed class ContextProvider(
    TimeProvider timeProvider,
    IEnumerable<AITool> tools,
    IMessageStore? messageStore = null
) : IContextProvider
{
    readonly Lock sync = new();
    readonly List<string> systemPrompts = [];
    readonly List<AITool> manualTools = [];
    readonly List<string> quietWords = [..AiContext.DefaultQuietWords];

    public void AddSystemPrompt(string prompt)
    {
        lock (this.sync)
            this.systemPrompts.Add(prompt);
    }

    public bool RemoveSystemPrompt(string prompt)
    {
        lock (this.sync)
            return this.systemPrompts.Remove(prompt);
    }

    public void ClearSystemPrompts()
    {
        lock (this.sync)
            this.systemPrompts.Clear();
    }

    public void AddTool(AITool tool)
    {
        lock (this.sync)
            this.manualTools.Add(tool);
    }

    public bool RemoveTool(AITool tool)
    {
        lock (this.sync)
            return this.manualTools.Remove(tool);
    }

    public void ClearTools()
    {
        lock (this.sync)
            this.manualTools.Clear();
    }

    public void AddQuietWord(string word)
    {
        lock (this.sync)
            this.quietWords.Add(word);
    }

    public bool RemoveQuietWord(string word)
    {
        lock (this.sync)
            return this.quietWords.Remove(word);
    }

    public void ClearQuietWords()
    {
        lock (this.sync)
            this.quietWords.Clear();
    }

    public void SetQuietWords(IEnumerable<string> words)
    {
        lock (this.sync)
        {
            this.quietWords.Clear();
            this.quietWords.AddRange(words);
        }
    }

    public IReadOnlyList<string> GetQuietWords()
    {
        lock (this.sync)
            return this.quietWords.ToList();
    }

    public Task Apply(AiContext context)
    {
        if (context.Acknowledgement == AiAcknowledgement.LessWordy)
            context.SystemPrompts.Add("Be concise and brief in your responses. Avoid unnecessary elaboration.");

        context.SystemPrompts.Add($"The current time is {timeProvider.GetUtcNow().ToLocalTime():hh:mm tt} on {timeProvider.GetUtcNow().ToLocalTime():MMMM dd, yyyy}.");
        context.Tools.AddRange(tools);
        
        if (messageStore != null)
            context.Tools.Add(new ChatLookupAITool(messageStore).AsTool());

        lock (this.sync)
        {
            context.SystemPrompts.AddRange(this.systemPrompts);
            context.Tools.AddRange(this.manualTools);

            context.QuietWords?.Clear();
            context.QuietWords?.AddRange(this.quietWords);
        }
        return Task.CompletedTask;
    }
}
