using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Maui.Controls.Chat;

namespace Shiny.AiConversation.Maui;

/// <summary>
/// A drop-in chat surface for <see cref="IAiConversationService"/>. It is a
/// <see cref="ChatView"/> - every style, template and behavior property of the base control still
/// applies - with the provider, session, history paging and live AI events already wired up.
/// </summary>
/// <example>
/// <code>
/// &lt;ai:AiChatView BotName="Aura"
///                BotAvatar="bot.png"
///                MyBubbleColor="{StaticResource Primary}"
///                GreetingMessage="Hi! What can I help you with?" /&gt;
/// </code>
/// </example>
public class AiChatView : ChatView
{
    readonly AiChatSettings settings = new();
    AiMicrophoneInputAction? micAction;
    IAiConversationService? resolvedService;

    public AiChatView() => this.SessionId = AiChatSettings.DefaultSessionId;

    /// <summary>The live settings backing the chat session - kept in sync with the bindable properties below.</summary>
    public AiChatSettings Settings => this.settings;


    /// <summary>
    /// Tears down and reloads the conversation - use after clearing chat history so the control
    /// re-queries the message store.
    /// </summary>
    public void Refresh()
    {
        var provider = this.Provider;
        if (provider is null)
            return;

        this.Provider = null;
        this.Provider = provider;
    }


    // ---- ai service ----

    public static readonly BindableProperty AiServiceProperty = BindableProperty.Create(
        nameof(AiService),
        typeof(IAiConversationService),
        typeof(AiChatView),
        null,
        propertyChanged: (b, _, _) => ((AiChatView)b).RebuildProvider()
    );

    /// <summary>
    /// The conversation service driving the chat. Leave unset to resolve <see cref="IAiConversationService"/>
    /// from the app's service provider.
    /// </summary>
    public IAiConversationService? AiService
    {
        get => (IAiConversationService?)GetValue(AiServiceProperty);
        set => SetValue(AiServiceProperty, value);
    }


    // ---- bot / user identity ----

    public static readonly BindableProperty BotNameProperty = BindableProperty.Create(
        nameof(BotName), typeof(string), typeof(AiChatView), "Assistant",
        propertyChanged: OnSettingChanged
    );

    /// <summary>Display name of the AI. Also used as the chat session name.</summary>
    public string BotName
    {
        get => (string)GetValue(BotNameProperty);
        set => SetValue(BotNameProperty, value);
    }

    public static readonly BindableProperty BotAvatarProperty = BindableProperty.Create(
        nameof(BotAvatar), typeof(ImageSource), typeof(AiChatView), null,
        propertyChanged: OnSettingChanged
    );

    /// <summary>Avatar shown for the AI.</summary>
    public ImageSource? BotAvatar
    {
        get => (ImageSource?)GetValue(BotAvatarProperty);
        set => SetValue(BotAvatarProperty, value);
    }

    public static readonly BindableProperty BotBubbleColorProperty = BindableProperty.Create(
        nameof(BotBubbleColor), typeof(Color), typeof(AiChatView), null,
        propertyChanged: OnSettingChanged
    );

    /// <summary>Bubble color for AI messages. Leave unset to use <see cref="ChatView.OtherBubbleColor"/>.</summary>
    public Color? BotBubbleColor
    {
        get => (Color?)GetValue(BotBubbleColorProperty);
        set => SetValue(BotBubbleColorProperty, value);
    }

    public static readonly BindableProperty UserNameProperty = BindableProperty.Create(
        nameof(UserName), typeof(string), typeof(AiChatView), "Me",
        propertyChanged: OnSettingChanged
    );

    /// <summary>Display name of the device user.</summary>
    public string UserName
    {
        get => (string)GetValue(UserNameProperty);
        set => SetValue(UserNameProperty, value);
    }

    public static readonly BindableProperty UserAvatarProperty = BindableProperty.Create(
        nameof(UserAvatar), typeof(ImageSource), typeof(AiChatView), null,
        propertyChanged: OnSettingChanged
    );

    /// <summary>Avatar shown for the device user.</summary>
    public ImageSource? UserAvatar
    {
        get => (ImageSource?)GetValue(UserAvatarProperty);
        set => SetValue(UserAvatarProperty, value);
    }

    public static readonly BindableProperty UserBubbleColorProperty = BindableProperty.Create(
        nameof(UserBubbleColor), typeof(Color), typeof(AiChatView), null,
        propertyChanged: OnSettingChanged
    );

    /// <summary>Bubble color for the device user. Leave unset to use <see cref="ChatView.MyBubbleColor"/>.</summary>
    public Color? UserBubbleColor
    {
        get => (Color?)GetValue(UserBubbleColorProperty);
        set => SetValue(UserBubbleColorProperty, value);
    }


    // ---- history / content ----

    public static readonly BindableProperty LoadHistoryProperty = BindableProperty.Create(
        nameof(LoadHistory), typeof(bool), typeof(AiChatView), true,
        propertyChanged: OnSettingChanged
    );

    /// <summary>
    /// Backfills the chat from the registered <see cref="IMessageStore"/> and pages further back on
    /// scroll-to-top. When no message store is registered the chat starts empty and stays live only.
    /// </summary>
    public bool LoadHistory
    {
        get => (bool)GetValue(LoadHistoryProperty);
        set => SetValue(LoadHistoryProperty, value);
    }

    public static readonly BindableProperty ShowTokenUsageProperty = BindableProperty.Create(
        nameof(ShowTokenUsage), typeof(bool), typeof(AiChatView), false,
        propertyChanged: OnSettingChanged
    );

    /// <summary>Appends a token usage footer to AI messages when the provider reports usage.</summary>
    public bool ShowTokenUsage
    {
        get => (bool)GetValue(ShowTokenUsageProperty);
        set => SetValue(ShowTokenUsageProperty, value);
    }

    public static readonly BindableProperty ShowChoiceButtonsProperty = BindableProperty.Create(
        nameof(ShowChoiceButtons), typeof(bool), typeof(AiChatView), true,
        propertyChanged: (b, _, _) =>
        {
            var view = (AiChatView)b;
            view.SyncSettings();
            view.ApplyChoiceTemplate();
        }
    );

    /// <summary>
    /// Renders a button per <see cref="AiChoice"/> under AI bubbles that ask a question with a fixed set
    /// of answers; tapping one sends its label back as the user's answer. Requires structured output on
    /// the conversation service (the default). Setting your own
    /// <see cref="ChatView.MessageTemplateSelector"/> or <see cref="ChatView.MessageTemplate"/> takes
    /// precedence - the built-in selector is only installed when neither is set.
    /// </summary>
    public bool ShowChoiceButtons
    {
        get => (bool)GetValue(ShowChoiceButtonsProperty);
        set => SetValue(ShowChoiceButtonsProperty, value);
    }

    public static readonly BindableProperty ChoiceSendTextProperty = BindableProperty.Create(
        nameof(ChoiceSendText), typeof(string), typeof(AiChatView), "Send"
    );

    /// <summary>Label of the commit button shown for questions that allow more than one choice.</summary>
    public string ChoiceSendText
    {
        get => (string)GetValue(ChoiceSendTextProperty);
        set => SetValue(ChoiceSendTextProperty, value);
    }

    public static readonly BindableProperty GreetingMessageProperty = BindableProperty.Create(
        nameof(GreetingMessage), typeof(string), typeof(AiChatView), null,
        propertyChanged: OnSettingChanged
    );

    /// <summary>Message shown from the AI when there is no history to display.</summary>
    public string? GreetingMessage
    {
        get => (string?)GetValue(GreetingMessageProperty);
        set => SetValue(GreetingMessageProperty, value);
    }


    // ---- voice ----

    public static readonly BindableProperty ShowMicrophoneActionProperty = BindableProperty.Create(
        nameof(ShowMicrophoneAction), typeof(bool), typeof(AiChatView), false,
        propertyChanged: (b, _, _) => ((AiChatView)b).ApplyMicrophoneAction()
    );

    /// <summary>
    /// Adds a push-to-talk action to the input bar's overflow actions that hands the microphone to
    /// <see cref="IAiConversationService.ListenAndTalk"/>. Voice turns started elsewhere (wake word)
    /// appear in the chat regardless of this setting.
    /// </summary>
    public bool ShowMicrophoneAction
    {
        get => (bool)GetValue(ShowMicrophoneActionProperty);
        set => SetValue(ShowMicrophoneActionProperty, value);
    }

    public static readonly BindableProperty MicrophoneActionTextProperty = BindableProperty.Create(
        nameof(MicrophoneActionText), typeof(string), typeof(AiChatView), "🎤 Voice Input",
        propertyChanged: (b, _, _) => ((AiChatView)b).ApplyMicrophoneAction()
    );

    /// <summary>Label of the push-to-talk action.</summary>
    public string MicrophoneActionText
    {
        get => (string)GetValue(MicrophoneActionTextProperty);
        set => SetValue(MicrophoneActionTextProperty, value);
    }


    // ---- wiring ----

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (this.Handler is null)
            return;

        this.SyncSettings();
        this.ApplyMicrophoneAction();
        this.ApplyChoiceTemplate();
        this.EnsureProvider();
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);

        // SessionId lives on the base control - mirror it so Info reports the right session.
        if (propertyName == nameof(this.SessionId))
            this.SyncSettings();
    }

    static void OnSettingChanged(BindableObject bindable, object? oldValue, object? newValue)
        => ((AiChatView)bindable).SyncSettings();

    void SyncSettings()
    {
        this.settings.SessionId = String.IsNullOrWhiteSpace(this.SessionId) ? AiChatSettings.DefaultSessionId : this.SessionId!;
        this.settings.BotName = this.BotName;
        this.settings.BotAvatar = this.BotAvatar;
        this.settings.BotBubbleColor = this.BotBubbleColor;
        this.settings.UserName = this.UserName;
        this.settings.UserAvatar = this.UserAvatar;
        this.settings.UserBubbleColor = this.UserBubbleColor;
        this.settings.LoadHistory = this.LoadHistory;
        this.settings.ShowTokenUsage = this.ShowTokenUsage;
        this.settings.ShowChoiceButtons = this.ShowChoiceButtons;
        this.settings.GreetingMessage = this.GreetingMessage;
        this.settings.NotifyChanged();
    }

    void ApplyChoiceTemplate()
    {
        if (!this.ShowChoiceButtons)
        {
            if (this.MessageTemplateSelector is AiChoiceTemplateSelector)
                this.MessageTemplateSelector = null;

            return;
        }

        // Never stomp a consumer-supplied template - they own the bubble in that case, and can still
        // read the choices off ChatMessage.Metadata via AiChoiceTemplateSelector.ReadQuestions.
        if (this.MessageTemplateSelector is null && this.MessageTemplate is null)
            this.MessageTemplateSelector = new AiChoiceTemplateSelector(this);
    }

    /// <summary>
    /// Routes a tapped choice into the live session as the user's answer.
    /// </summary>
    internal Task SendChoiceAnswerAsync(string answer)
        => this.Provider is AiChatSessionProvider { Current: { } session }
            ? session.SendChoiceAnswerAsync(answer)
            : Task.CompletedTask;

    void RebuildProvider()
    {
        this.resolvedService = null;
        this.Provider = null;
        this.EnsureProvider();
    }

    void EnsureProvider()
    {
        var ai = this.AiService
            ?? this.Handler?.MauiContext?.Services.GetService<IAiConversationService>()
            ?? Application.Current?.Handler?.MauiContext?.Services.GetService<IAiConversationService>();

        if (ai is null || ReferenceEquals(ai, this.resolvedService))
            return;

        this.resolvedService = ai;
        if (this.micAction is not null)
            this.micAction.AiService = ai;

        this.Provider = new AiChatSessionProvider(ai, this.settings);
    }

    void ApplyMicrophoneAction()
    {
        var actions = this.InputActions ?? new ObservableCollection<ChatInputAction>();

        if (this.ShowMicrophoneAction)
        {
            this.micAction ??= new AiMicrophoneInputAction();
            this.micAction.ListenText = this.MicrophoneActionText;
            this.micAction.Text = this.MicrophoneActionText;
            this.micAction.AiService = this.resolvedService;

            if (actions.Contains(this.micAction))
                return;

            actions.Insert(0, this.micAction);
        }
        else if (this.micAction is null || !actions.Remove(this.micAction))
        {
            return;
        }

        // Only re-assign when the list actually changed - the input bar re-evaluates whether it needs
        // the overflow button on assignment, and leaving it alone otherwise keeps any binding intact.
        this.InputActions = new ObservableCollection<ChatInputAction>(actions);
    }
}
