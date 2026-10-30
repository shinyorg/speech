namespace Shiny.AiConversation.Maui;

/// <summary>
/// The presentation settings for an AI chat - who the bot is, how its bubbles look, and how much history is
/// pulled in. <see cref="AiChatView"/> owns an instance and keeps it in sync with its bindable properties.
/// </summary>
public class AiChatSettings
{
    /// <summary>The sender id used for messages from the device user.</summary>
    public const string UserId = "user";

    /// <summary>The sender id used for messages from the AI.</summary>
    public const string BotId = "assistant";

    /// <summary>The display name of the AI. Also used as the chat session name.</summary>
    public string BotName { get; set; } = "Assistant";

    /// <summary>The avatar shown for the AI.</summary>
    public ImageSource? BotAvatar { get; set; }

    /// <summary>Bubble color for AI messages. Null falls back to the chat control's OtherBubbleColor.</summary>
    public Color? BotBubbleColor { get; set; }

    /// <summary>The display name of the device user.</summary>
    public string UserName { get; set; } = "Me";

    /// <summary>The avatar shown for the device user.</summary>
    public ImageSource? UserAvatar { get; set; }

    /// <summary>Bubble color for the device user. Null falls back to the chat control's MyBubbleColor.</summary>
    public Color? UserBubbleColor { get; set; }

    /// <summary>
    /// When true (default) the chat is backfilled from the session's stored history and scrolling up pages further back.
    /// </summary>
    public bool LoadHistory { get; set; } = true;

    /// <summary>
    /// Lets the user attach photos to messages (sent to the model as images).
    /// </summary>
    public bool AllowImages { get; set; }

    /// <summary>
    /// How long attached photos stay in the model's context.
    /// </summary>
    public ImageRetention ImageRetention { get; set; } = ImageRetention.DropAfterTurn;

    /// <summary>
    /// For replies cut off while being spoken, show only the part that was heard.
    /// </summary>
    public bool ShowSpokenPartOfInterruptedReplies { get; set; }

    /// <summary>Appends a token usage footer to each AI message when the provider reports usage.</summary>
    public bool ShowTokenUsage { get; set; }

    /// <summary>
    /// Renders tappable buttons under the AI bubble that asked a question with <see cref="AiQuestion.Choices"/>.
    /// </summary>
    public bool ShowChoiceButtons { get; set; } = true;

    /// <summary>Optional message shown from the AI when there is no history to display.</summary>
    public string? GreetingMessage { get; set; }

    /// <summary>Prefix applied to AI bubbles that carry an error.</summary>
    public string ErrorPrefix { get; set; } = "⚠️ ";

    /// <summary>Raised when any of the above change so the live session can push a session update to the control.</summary>
    public event EventHandler? Changed;

    /// <summary>Notifies listeners (the live chat session) that settings changed.</summary>
    public void NotifyChanged() => this.Changed?.Invoke(this, EventArgs.Empty);
}
