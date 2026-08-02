using Shiny.Maui.Controls.Chat;

namespace Shiny.AiConversation.Maui;

/// <summary>
/// Selects the choice-button bubble for AI messages that carry <see cref="AiQuestion.Choices"/>, and
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
        => item is ChatMessage message && ReadQuestions(message) is { Length: > 0 }
            ? this.choiceTemplate
            : null; // null falls back to the control's own bubble rendering

    /// <summary>Reads the questions attached to a message, or null when it carries none.</summary>
    public static AiQuestion[]? ReadQuestions(ChatMessage message)
        => message.Metadata is { } metadata && metadata.TryGetValue(QuestionsMetadataKey, out var json)
            ? AiTurnSerializer.DeserializeQuestions(json)
            : null;

    internal bool IsAnswered(string messageId) => this.answered.Contains(messageId);

    internal void MarkAnswered(string messageId) => this.answered.Add(messageId);

    internal Task SendAnswerAsync(string answer)
        => this.chatView.SendChoiceAnswerAsync(answer);
}
