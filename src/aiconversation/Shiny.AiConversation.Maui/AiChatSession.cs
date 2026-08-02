using System.Collections.Concurrent;
using Shiny.Maui.Controls.Chat;

namespace Shiny.AiConversation.Maui;

/// <summary>
/// A live chat session over <see cref="IAiConversationService"/>. Typed messages are forwarded to
/// <see cref="IAiConversationService.TalkTo"/>; AI replies arrive on <see cref="IAiConversationService.AiResponded"/>
/// and voice turns (wake word or push-to-talk) arrive on <see cref="IAiConversationService.SpeechOccurred"/>,
/// both of which are pushed to the control as received messages. History paging maps onto the registered
/// <see cref="IMessageStore"/> via <see cref="IAiConversationService.GetChatHistory"/>.
/// </summary>
sealed class AiChatSession : IChatSession
{
    // ChatView drops a typing indicator that hasn't been refreshed for 6s, so a long "thinking" turn
    // needs a heartbeat to keep the bubble alive.
    static readonly TimeSpan TypingHeartbeat = TimeSpan.FromSeconds(3);

    readonly IAiConversationService ai;
    readonly AiChatSettings settings;
    readonly DateTimeOffset createdAt = DateTimeOffset.Now;

    // Maps message ids to their timestamps so cursor based paging can resolve an "older than" bound.
    readonly ConcurrentDictionary<string, DateTimeOffset> timestamps = new(StringComparer.Ordinal);
    readonly Timer typingTimer;

    bool historyAvailable = true;
    bool isTyping;

    public AiChatSession(IAiConversationService ai, AiChatSettings settings)
    {
        this.ai = ai;
        this.settings = settings;
        this.typingTimer = new Timer(_ => this.RaiseTyping(true), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        this.ai.AiResponded += this.OnAiResponded;
        this.ai.SpeechOccurred += this.OnSpeechOccurred;
        this.ai.StatusChanged += this.OnStatusChanged;
        this.ai.ErrorOccurred += this.OnErrorOccurred;
        this.settings.Changed += this.OnSettingsChanged;
    }


    public string CurrentUserId => AiChatSettings.UserId;

    public ChatSessionInfo Info => new(
        SessionId: this.settings.SessionId,
        SessionName: this.settings.BotName,
        Users:
        [
            new ChatSessionUserInfo(AiChatSettings.UserId, this.settings.UserName, this.settings.UserAvatar, this.settings.UserBubbleColor, this.createdAt),
            new ChatSessionUserInfo(AiChatSettings.BotId, this.settings.BotName, this.settings.BotAvatar, this.settings.BotBubbleColor, this.createdAt)
        ],
        PermittedEmojis: [],                             // an AI chat has no reactions
        BodyPermissions: MessageBodyPermissions.None,    // plain-text input (AI replies still render markdown)
        Permissions: ChatSessionPermissions.CanSendMessages,
        CreatedAt: this.createdAt,
        LastReadDate: DateTimeOffset.Now,
        UnreadMessageCount: 0
    );

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<UserTypingEvent>? UserTyping;
    public event EventHandler<ChatSessionInfo>? SessionUpdated;

    // An AI chat never edits, deletes or reacts, and it is never offline from the control's point of
    // view - these are part of the interface but intentionally never raised.
#pragma warning disable CS0067
    public event EventHandler<MessageChanged>? MessageUpdated;
    public event EventHandler<string>? MessageDeleted;
    public event EventHandler<ChatSessionUserInfo>? UserJoined;
    public event EventHandler<ChatSessionUserInfo>? UserLeft;
    public event EventHandler<ChatConnectionState>? ConnectionStateChanged;
#pragma warning restore CS0067


    // ---- history paging (cursor based, older only) ----

    public async Task<MessagePage> GetMessagesAsync(
        string? cursorMessageId,
        MessagePageDirection direction,
        int count,
        CancellationToken cancellationToken = default
    )
    {
        if (direction == MessagePageDirection.Newer)
            return new MessagePage([], false);

        var initial = cursorMessageId is null;
        if (!this.settings.LoadHistory || !this.historyAvailable)
            return new MessagePage(this.Greeting(initial), false);

        DateTimeOffset? endDate = null;
        if (cursorMessageId is not null && this.timestamps.TryGetValue(cursorMessageId, out var ts))
            endDate = ts.AddTicks(-1); // strictly older than the cursor

        IReadOnlyList<AiChatMessage> history;
        try
        {
            history = await this.ai
                .GetChatHistory(endDate: endDate, limit: count)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // no IMessageStore registered - the chat simply starts empty and stays live-only
            this.historyAvailable = false;
            return new MessagePage(this.Greeting(initial), false);
        }

        if (history.Count == 0)
            return new MessagePage(this.Greeting(initial), false);

        // GetChatHistory returns ascending by timestamp
        var mapped = history.Select(this.Map).ToList();
        return new MessagePage(mapped, history.Count >= count);
    }


    // ---- outgoing ----

    public Task<ChatMessage> SendMessageAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        // Images aren't advertised (CanSendImages is off), but the provider owns any attachment stream regardless.
        message.Attachment?.Content.Dispose();

        var sent = this.CreateMessage(
            AiChatSettings.UserId,
            message.Body,
            String.IsNullOrEmpty(message.ClientMessageId) ? null : message.ClientMessageId
        );

        // Fire the AI round trip in the background; the reply surfaces via AiResponded -> MessageReceived.
        _ = this.TalkAsync(message.Body ?? String.Empty);
        return Task.FromResult(sent);
    }

    async Task TalkAsync(string text)
    {
        try
        {
            await this.ai.TalkTo(text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            this.PushError(ex);
        }
    }


    // ---- ai service events ----

    void OnAiResponded(AiResponse response)
    {
        this.SetTyping(false);

        // AiResponse.Text is the parsed reply when structured output is in play - Response.Text would be
        // the raw JSON envelope.
        if (response.Text is not { } text || String.IsNullOrWhiteSpace(text))
            return;

        var usage = response.Response.Usage;
        var body = this.settings.ShowTokenUsage
            ? AiChatTokens.AppendTokenFooter(text, usage?.InputTokenCount, usage?.OutputTokenCount, usage?.TotalTokenCount)
            : text;

        this.Push(this.CreateMessage(AiChatSettings.BotId, body, metadata: BuildChoiceMetadata(response, this.settings)));
    }

    static IReadOnlyDictionary<string, string>? BuildChoiceMetadata(AiResponse response, AiChatSettings settings)
    {
        if (!settings.ShowChoiceButtons)
            return null;

        var withChoices = response.Questions.Where(x => x.HasChoices).ToArray();
        return withChoices.Length == 0
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AiChoiceTemplateSelector.QuestionsMetadataKey] = AiTurnSerializer.SerializeQuestions(withChoices)
            };
    }

    /// <summary>
    /// Sends a tapped choice as the user's answer - the bubble is pushed here because the control only
    /// creates one for text the user typed into the entry.
    /// </summary>
    internal Task SendChoiceAnswerAsync(string answer)
    {
        if (String.IsNullOrWhiteSpace(answer))
            return Task.CompletedTask;

        this.Push(this.CreateMessage(AiChatSettings.UserId, answer));
        return this.TalkAsync(answer);
    }

    void OnSpeechOccurred(ConversationSpeech speech)
    {
        // Only the heard side becomes a bubble - the spoken side is the AI response already pushed by
        // OnAiResponded. Typed messages don't raise this event, so there is nothing to de-duplicate.
        if (speech.Source != ConversationSpeechSource.Heard || String.IsNullOrWhiteSpace(speech.Text))
            return;

        this.Push(this.CreateMessage(AiChatSettings.UserId, speech.Text));
    }

    void OnStatusChanged(AiState state)
        => this.SetTyping(state is AiState.Thinking or AiState.Responding);

    void OnErrorOccurred(Exception ex) => this.PushError(ex);

    void OnSettingsChanged(object? sender, EventArgs args)
        => this.SessionUpdated?.Invoke(this, this.Info);


    // ---- typing indicator ----

    void SetTyping(bool typing)
    {
        if (this.isTyping == typing)
            return;

        this.isTyping = typing;
        this.RaiseTyping(typing);
        this.typingTimer.Change(
            typing ? TypingHeartbeat : Timeout.InfiniteTimeSpan,
            typing ? TypingHeartbeat : Timeout.InfiniteTimeSpan
        );
    }

    void RaiseTyping(bool typing)
        => this.UserTyping?.Invoke(this, new UserTypingEvent(AiChatSettings.BotId, typing, DateTimeOffset.UtcNow));


    // ---- message helpers ----

    ChatMessage CreateMessage(
        string senderId,
        string? body,
        string? clientMessageId = null,
        string? identifier = null,
        IReadOnlyDictionary<string, string>? metadata = null
    )
    {
        var now = DateTimeOffset.Now;
        var id = Guid.NewGuid().ToString("N");
        this.timestamps[id] = now;

        return new ChatMessage(
            MessageId: id,
            ClientMessageId: clientMessageId,
            SenderId: senderId,
            Body: body,
            ImageUrl: null,
            Status: MessageStatus.Sent,
            StatusReason: null,
            Timestamp: now,
            EditedTimestamp: null,
            Reactions: [],
            ReadReceipts: [],
            Identifier: identifier,
            Metadata: metadata
        );
    }

    void Push(ChatMessage message)
        => this.MessageReceived?.Invoke(this, message); // control marshals to the UI thread

    void PushError(Exception ex)
    {
        this.SetTyping(false);
        this.Push(this.CreateMessage(AiChatSettings.BotId, this.settings.ErrorPrefix + ex.Message, identifier: "error"));
    }

    IReadOnlyList<ChatMessage> Greeting(bool initialPage)
    {
        if (!initialPage || String.IsNullOrWhiteSpace(this.settings.GreetingMessage))
            return [];

        return [this.CreateMessage(AiChatSettings.BotId, this.settings.GreetingMessage, identifier: "greeting")];
    }

    ChatMessage Map(AiChatMessage msg)
    {
        this.timestamps[msg.Id] = msg.Timestamp;

        var isAi = msg.Direction == ChatMessageDirection.AI;
        var body = isAi && this.settings.ShowTokenUsage
            ? AiChatTokens.AppendTokenFooter(msg.Message, msg.InputTokens, msg.OutputTokens, msg.TotalTokens)
            : msg.Message;

        return new ChatMessage(
            MessageId: msg.Id,
            ClientMessageId: null,
            SenderId: isAi ? AiChatSettings.BotId : AiChatSettings.UserId,
            Body: body,
            ImageUrl: null,
            Status: MessageStatus.Read,
            StatusReason: null,
            Timestamp: msg.Timestamp,
            EditedTimestamp: null,
            Reactions: [],
            ReadReceipts: []
        );
    }


    // ---- not applicable to an AI chat (permissions gate these off, so the control never calls them) ----

    public Task<ChatMessage> ResendMessageAsync(string clientMessageId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task EditMessageAsync(string messageId, string body, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteMessageAsync(string messageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReactToMessageAsync(string messageId, string emoji, bool add, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task MarkReadAsync(string[] messageIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ToggleTypingAsync(bool isTyping, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task InviteUserAsync(string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task LeaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RenameAsync(string sessionName, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        this.ai.AiResponded -= this.OnAiResponded;
        this.ai.SpeechOccurred -= this.OnSpeechOccurred;
        this.ai.StatusChanged -= this.OnStatusChanged;
        this.ai.ErrorOccurred -= this.OnErrorOccurred;
        this.settings.Changed -= this.OnSettingsChanged;
        this.typingTimer.Dispose();

        return ValueTask.CompletedTask;
    }
}
