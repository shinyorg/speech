namespace Shiny.AiConversation.Maui;

/// <summary>
/// The presentation settings for an AI backed chat session - who the bot is, how its bubbles look,
/// and how much history is pulled in. <see cref="AiChatView"/> owns an instance of this and keeps it
/// in sync with its bindable properties; it can also be configured at registration time when using
/// <see cref="AiChatSessionProvider"/> directly against a plain <c>ChatView</c>.
/// </summary>
public class AiChatSettings
{
    /// <summary>The session id used when nothing else is supplied - the AI conversation is a single, global session.</summary>
    public const string DefaultSessionId = "ai";

    /// <summary>The sender id used for messages from the device user.</summary>
    public const string UserId = "user";

    /// <summary>The sender id used for messages from the AI.</summary>
    public const string BotId = "assistant";

    /// <summary>The session id reported back to the chat control.</summary>
    public string SessionId { get; set; } = DefaultSessionId;

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
    /// When true (default) the chat is backfilled from the registered <see cref="IMessageStore"/> and
    /// scrolling up pages further back. When no message store is registered, the chat simply starts empty.
    /// </summary>
    public bool LoadHistory { get; set; } = true;

    /// <summary>Appends a token usage footer to each AI message when the provider reports usage.</summary>
    public bool ShowTokenUsage { get; set; }

    /// <summary>
    /// Renders tappable buttons under an AI bubble when the turn carries <see cref="AiQuestion.Choices"/>.
    /// Requires structured output to be enabled on the conversation service (the default).
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
