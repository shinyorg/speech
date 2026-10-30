using Shiny.Maui.Controls.Chat;

namespace Shiny.AiConversation.Maui;

/// <summary>
/// Selects the choice-button bubble for the AI message that asked questions with <see cref="AiQuestion.Choices"/>, and
/// falls through to the control's default rendering for everything else.
/// </summary>
/// <remarks>
/// <see cref="AiChatView"/> installs this automatically when
/// <see cref="AiChatView.ShowChoiceButtons"/> is set and no template selector of your own is in place.
/// </remarks>
public class AiChoiceTemplateSelector : DataTemplateSelector
{
    /// <summary>The message metadata key carrying the JSON-encoded questions for a bubble.</summary>
    public const string QuestionsMetadataKey = "shiny.ai.questions";

    readonly AiChatView chatView;
    readonly DataTemplate choiceTemplate;
    readonly HashSet<string> answered = new(StringComparer.Ordinal);

    internal AiChoiceTemplateSelector(AiChatView chatView)
    {
        this.chatView = chatView;
        this.choiceTemplate = new DataTemplate(() => new AiChoiceBubbleView(chatView, this));
    }

    /// <inheritdoc />
    protected override DataTemplate? OnSelectTemplate(object item, BindableObject container)
        => item is ChatMessage message && ReadQuestions(message) is { Count: > 0 }
            ? this.choiceTemplate
            : null; // null falls back to the control's own bubble rendering

    /// <summary>Reads the questions attached to a message, or null when it carries none.</summary>
    public static IReadOnlyList<AiQuestion>? ReadQuestions(ChatMessage message)
    {
        if (message.Metadata is not { } metadata || !metadata.TryGetValue(QuestionsMetadataKey, out var json))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(json, AiConversationJsonContext.Default.ListAiQuestion);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    internal bool IsAnswered(string messageId) => this.answered.Contains(messageId);

    internal void MarkAnswered(string messageId) => this.answered.Add(messageId);

    internal Task SendAnswerAsync(AiQuestion question, IReadOnlyList<string> choiceIds)
        => this.chatView.SendChoiceAnswerAsync(question, choiceIds);
}
