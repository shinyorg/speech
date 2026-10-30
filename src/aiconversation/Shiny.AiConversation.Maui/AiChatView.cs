using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AiConversation.Voice;
using Shiny.Maui.Controls.Chat;

namespace Shiny.AiConversation.Maui;

/// <summary>
/// A drop-in chat surface for an <see cref="IAiSession"/>. It is a <see cref="ChatView"/> - every style, template
/// and behavior property of the base control still applies - with the provider, history paging, streaming
/// replies and choice chips already wired up.
/// </summary>
/// <remarks>
/// Bind <see cref="Session"/> to share one session with the rest of the app (e.g. the voice conversation). Leave it
/// unset and the view resumes the most recent session (or starts one) itself, and owns that instance.
/// </remarks>
public class AiChatView : ChatView
{
    readonly AiChatSettings settings = new();
    AiMicrophoneInputAction? micAction;
    AiChatSessionProvider? provider;
    IAiSession? ownedSession;
    IAiSession? boundSession;
    ContextPressure? lastPressure;

    /// <summary>The live settings backing the chat, kept in sync with the bindable properties below.</summary>
    public AiChatSettings Settings => this.settings;

    /// <summary>The session currently shown.</summary>
    public IAiSession? CurrentSession => this.boundSession;

    /// <summary>
    /// Raised when the session's context fills up (Warning / Critical) or is compacted back to Normal.
    /// </summary>
    public event EventHandler<ContextSnapshot>? ContextPressureChanged;


    /// <summary>Reloads the conversation from the store.</summary>
    public void Refresh()
    {
        var current = this.Provider;
        if (current is null)
            return;

        this.Provider = null;
        this.Provider = current;
    }

    /// <summary>
    /// Starts a fresh session (when the view owns its session) and shows it.
    /// </summary>
    public async Task NewConversation()
    {
        if (this.Session != null)
            throw new InvalidOperationException("The Session is bound; start a new one where it is owned");

        var manager = this.Services?.GetService<IAiSessionManager>() ?? throw new InvalidOperationException("IAiSessionManager is not registered");
        var old = this.ownedSession;
        this.ownedSession = await manager.Start().ConfigureAwait(true);
        this.Bind(this.ownedSession);
        if (old != null)
            await old.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>Compacts the current session (the "this conversation is getting long" action).</summary>
    public Task Compact() => this.boundSession?.Compact() ?? Task.CompletedTask;


    // ---- session ----

    public static readonly BindableProperty SessionProperty = BindableProperty.Create(
        nameof(Session), typeof(IAiSession), typeof(AiChatView), null,
        propertyChanged: (b, _, n) => ((AiChatView)b).OnSessionChanged((IAiSession?)n)
    );

    /// <summary>
    /// The session to show. Leave unset to resume the latest session (or start one) automatically.
    /// </summary>
    public IAiSession? Session
    {
        get => (IAiSession?)GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }


    // ---- bot / user identity ----

    public static readonly BindableProperty BotNameProperty = BindableProperty.Create(nameof(BotName), typeof(string), typeof(AiChatView), "Assistant", propertyChanged: OnSettingChanged);
    public string BotName { get => (string)GetValue(BotNameProperty); set => SetValue(BotNameProperty, value); }

    public static readonly BindableProperty BotAvatarProperty = BindableProperty.Create(nameof(BotAvatar), typeof(ImageSource), typeof(AiChatView), null, propertyChanged: OnSettingChanged);
    public ImageSource? BotAvatar { get => (ImageSource?)GetValue(BotAvatarProperty); set => SetValue(BotAvatarProperty, value); }

    public static readonly BindableProperty BotBubbleColorProperty = BindableProperty.Create(nameof(BotBubbleColor), typeof(Color), typeof(AiChatView), null, propertyChanged: OnSettingChanged);
    public Color? BotBubbleColor { get => (Color?)GetValue(BotBubbleColorProperty); set => SetValue(BotBubbleColorProperty, value); }

    public static readonly BindableProperty UserNameProperty = BindableProperty.Create(nameof(UserName), typeof(string), typeof(AiChatView), "Me", propertyChanged: OnSettingChanged);
    public string UserName { get => (string)GetValue(UserNameProperty); set => SetValue(UserNameProperty, value); }

    public static readonly BindableProperty UserAvatarProperty = BindableProperty.Create(nameof(UserAvatar), typeof(ImageSource), typeof(AiChatView), null, propertyChanged: OnSettingChanged);
    public ImageSource? UserAvatar { get => (ImageSource?)GetValue(UserAvatarProperty); set => SetValue(UserAvatarProperty, value); }

    public static readonly BindableProperty UserBubbleColorProperty = BindableProperty.Create(nameof(UserBubbleColor), typeof(Color), typeof(AiChatView), null, propertyChanged: OnSettingChanged);
    public Color? UserBubbleColor { get => (Color?)GetValue(UserBubbleColorProperty); set => SetValue(UserBubbleColorProperty, value); }


    // ---- content ----

    public static readonly BindableProperty LoadHistoryProperty = BindableProperty.Create(nameof(LoadHistory), typeof(bool), typeof(AiChatView), true, propertyChanged: OnSettingChanged);
    /// <summary>Backfills the chat from the session's history and pages further back on scroll-to-top.</summary>
    public bool LoadHistory { get => (bool)GetValue(LoadHistoryProperty); set => SetValue(LoadHistoryProperty, value); }

    public static readonly BindableProperty ShowTokenUsageProperty = BindableProperty.Create(nameof(ShowTokenUsage), typeof(bool), typeof(AiChatView), false, propertyChanged: OnSettingChanged);
    /// <summary>Appends a token usage footer to AI messages when the provider reports usage.</summary>
    public bool ShowTokenUsage { get => (bool)GetValue(ShowTokenUsageProperty); set => SetValue(ShowTokenUsageProperty, value); }

    public static readonly BindableProperty AllowImagesProperty = BindableProperty.Create(nameof(AllowImages), typeof(bool), typeof(AiChatView), false, propertyChanged: OnSettingChanged);
    /// <summary>Lets the user attach photos; they're sent to the model as images.</summary>
    public bool AllowImages { get => (bool)GetValue(AllowImagesProperty); set => SetValue(AllowImagesProperty, value); }

    public static readonly BindableProperty ShowContextWarningsProperty = BindableProperty.Create(nameof(ShowContextWarnings), typeof(bool), typeof(AiChatView), false);
    /// <summary>
    /// Shows an AI bubble (<c>Identifier = "context"</c>) when the conversation is getting long. Handle
    /// <see cref="ContextPressureChanged"/> to show your own banner instead.
    /// </summary>
    public bool ShowContextWarnings { get => (bool)GetValue(ShowContextWarningsProperty); set => SetValue(ShowContextWarningsProperty, value); }

    public static readonly BindableProperty ContextWarningTextProperty = BindableProperty.Create(nameof(ContextWarningText), typeof(string), typeof(AiChatView), "This conversation is getting long. Older messages will be summarized soon; start a new conversation for a fresh topic.");
    public string ContextWarningText { get => (string)GetValue(ContextWarningTextProperty); set => SetValue(ContextWarningTextProperty, value); }

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
    /// Renders a button per <see cref="AiChoice"/> under the AI bubble that asked; tapping one answers by choice id.
    /// Your own <see cref="ChatView.MessageTemplateSelector"/> / <see cref="ChatView.MessageTemplate"/> takes precedence.
    /// </summary>
    public bool ShowChoiceButtons { get => (bool)GetValue(ShowChoiceButtonsProperty); set => SetValue(ShowChoiceButtonsProperty, value); }

    public static readonly BindableProperty ChoiceSendTextProperty = BindableProperty.Create(nameof(ChoiceSendText), typeof(string), typeof(AiChatView), "Send");
    /// <summary>Label of the commit button for questions that allow more than one choice.</summary>
    public string ChoiceSendText { get => (string)GetValue(ChoiceSendTextProperty); set => SetValue(ChoiceSendTextProperty, value); }

    public static readonly BindableProperty GreetingMessageProperty = BindableProperty.Create(nameof(GreetingMessage), typeof(string), typeof(AiChatView), null, propertyChanged: OnSettingChanged);
    /// <summary>AI message shown when there is no history.</summary>
    public string? GreetingMessage { get => (string?)GetValue(GreetingMessageProperty); set => SetValue(GreetingMessageProperty, value); }


    // ---- voice ----

    public static readonly BindableProperty ShowMicrophoneActionProperty = BindableProperty.Create(nameof(ShowMicrophoneAction), typeof(bool), typeof(AiChatView), false, propertyChanged: (b, _, _) => ((AiChatView)b).ApplyMicrophoneAction());
    /// <summary>
    /// Adds a push-to-talk action to the input bar. Needs <c>AddShinyAiConversationVoice()</c> and speech providers.
    /// Voice turns started elsewhere (wake word) appear in the chat regardless.
    /// </summary>
    public bool ShowMicrophoneAction { get => (bool)GetValue(ShowMicrophoneActionProperty); set => SetValue(ShowMicrophoneActionProperty, value); }

    public static readonly BindableProperty MicrophoneActionTextProperty = BindableProperty.Create(nameof(MicrophoneActionText), typeof(string), typeof(AiChatView), "🎤 Voice Input", propertyChanged: (b, _, _) => ((AiChatView)b).ApplyMicrophoneAction());
    public string MicrophoneActionText { get => (string)GetValue(MicrophoneActionTextProperty); set => SetValue(MicrophoneActionTextProperty, value); }

    public static readonly BindableProperty VoiceOptionsProperty = BindableProperty.Create(nameof(VoiceOptions), typeof(VoiceOptions), typeof(AiChatView), null, propertyChanged: (b, _, _) => ((AiChatView)b).ApplyMicrophoneAction());
    /// <summary>Options used when the microphone action attaches the voice conversation.</summary>
    public VoiceOptions? VoiceOptions { get => (VoiceOptions?)GetValue(VoiceOptionsProperty); set => SetValue(VoiceOptionsProperty, value); }


    // ---- wiring ----

    IServiceProvider? Services => this.Handler?.MauiContext?.Services ?? Application.Current?.Handler?.MauiContext?.Services;

    protected override async void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (this.Handler is null)
        {
            // detached: release a session this view opened itself
            if (this.ownedSession is { } owned)
            {
                this.ownedSession = null;
                this.boundSession = null;
                this.Provider = null;
                await owned.DisposeAsync();
            }
            return;
        }

        this.ApplyRegisteredDefaults();
        this.SyncSettings();
        this.ApplyMicrophoneAction();
        this.ApplyChoiceTemplate();

        if (this.Session is { } bound)
        {
            this.Bind(bound);
        }
        else if (this.ownedSession == null && this.Services?.GetService<IAiSessionManager>() is { } manager)
        {
            try
            {
                this.ownedSession = await manager.GetLatest() ?? await manager.Start();
                if (this.Session == null)
                    this.Bind(this.ownedSession);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AiChatView: could not load a session: {ex.Message}");
            }
        }
    }

    void OnSessionChanged(IAiSession? session)
    {
        if (session != null)
            this.Bind(session);
    }

    void Bind(IAiSession session)
    {
        if (ReferenceEquals(session, this.boundSession))
            return;

        if (this.provider != null)
            this.provider.ContextPressureChanged -= this.OnContextPressure;

        this.boundSession = session;
        this.lastPressure = session.Context.Pressure;
        this.provider = new AiChatSessionProvider(session, this.settings, this.Services?.GetService<IAiAttachmentStore>());
        this.provider.ContextPressureChanged += this.OnContextPressure;
        this.SessionId = session.SessionId;
        this.ApplyMicrophoneAction();
        this.Provider = this.provider;
    }

    void OnContextPressure(object? sender, ContextSnapshot context)
    {
        var previous = this.lastPressure;
        this.lastPressure = context.Pressure;
        this.ContextPressureChanged?.Invoke(this, context);

        if (this.ShowContextWarnings && context.Pressure >= ContextPressure.Warning && context.Pressure != previous)
            this.provider?.Current?.PushNotice(this.ContextWarningText, "context");
    }

    /// <summary>
    /// Defaults registered with <c>ConfigureChatView</c> apply to every property this view doesn't set itself.
    /// </summary>
    void ApplyRegisteredDefaults()
    {
        if (this.Services?.GetService<AiChatSettings>() is not { } defaults || ReferenceEquals(defaults, this.settings))
            return;

        void Default(BindableProperty property, object? value)
        {
            if (!this.IsSet(property))
                this.SetValue(property, value);
        }

        Default(BotNameProperty, defaults.BotName);
        Default(BotAvatarProperty, defaults.BotAvatar);
        Default(BotBubbleColorProperty, defaults.BotBubbleColor);
        Default(UserNameProperty, defaults.UserName);
        Default(UserAvatarProperty, defaults.UserAvatar);
        Default(UserBubbleColorProperty, defaults.UserBubbleColor);
        Default(LoadHistoryProperty, defaults.LoadHistory);
        Default(ShowTokenUsageProperty, defaults.ShowTokenUsage);
        Default(ShowChoiceButtonsProperty, defaults.ShowChoiceButtons);
        Default(GreetingMessageProperty, defaults.GreetingMessage);
        Default(AllowImagesProperty, defaults.AllowImages);
        this.settings.ImageRetention = defaults.ImageRetention;
        this.settings.ShowSpokenPartOfInterruptedReplies = defaults.ShowSpokenPartOfInterruptedReplies;
        this.settings.ErrorPrefix = defaults.ErrorPrefix;
    }


    static void OnSettingChanged(BindableObject bindable, object? oldValue, object? newValue)
        => ((AiChatView)bindable).SyncSettings();

    void SyncSettings()
    {
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
        this.settings.AllowImages = this.AllowImages;
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

        // never stomp a consumer-supplied template; they can still read choices via AiChoiceTemplateSelector.ReadQuestions
        if (this.MessageTemplateSelector is null && this.MessageTemplate is null)
            this.MessageTemplateSelector = new AiChoiceTemplateSelector(this);
    }

    internal Task SendChoiceAnswerAsync(AiQuestion question, IReadOnlyList<string> choiceIds)
        => this.provider?.Current is { } chat ? chat.SendAnswerAsync(question, choiceIds) : Task.CompletedTask;

    void ApplyMicrophoneAction()
    {
        var actions = this.InputActions ?? new ObservableCollection<ChatInputAction>();

        if (this.ShowMicrophoneAction)
        {
            this.micAction ??= new AiMicrophoneInputAction();
            this.micAction.ListenText = this.MicrophoneActionText;
            this.micAction.Text = this.MicrophoneActionText;
            this.micAction.Session = this.boundSession;
            this.micAction.VoiceOptions = this.VoiceOptions;

            if (actions.Contains(this.micAction))
                return;

            actions.Insert(0, this.micAction);
        }
        else if (this.micAction is null || !actions.Remove(this.micAction))
        {
            return;
        }

        // only re-assign when the list changed: the input bar re-evaluates its overflow button on assignment
        this.InputActions = new ObservableCollection<ChatInputAction>(actions);
    }
}
