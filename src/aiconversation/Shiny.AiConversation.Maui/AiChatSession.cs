using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Shiny.Maui.Controls.Chat;
using ChatMessageStatus = Shiny.Maui.Controls.Chat.MessageStatus;
using ChatPage = Shiny.Maui.Controls.Chat.MessagePage;

namespace Shiny.AiConversation.Maui;

/// <summary>
/// A live chat session over one <see cref="IAiSession"/>. Session messages map 1:1 to bubbles: a streamed reply
/// is one bubble, created on its first text and updated in place as text arrives. History pages by sequence.
/// </summary>
sealed class AiChatSession : IChatSession
{
    // ChatView drops a typing indicator that hasn't been refreshed for 6s, so a long "thinking" turn
    // needs a heartbeat to keep the bubble alive (L11)
    static readonly TimeSpan TypingHeartbeat = TimeSpan.FromSeconds(3);
    static readonly TimeSpan StreamThrottle = TimeSpan.FromMilliseconds(100);

    readonly IAiSession session;
    readonly AiChatSettings settings;
    readonly IAiAttachmentStore? attachments;
    readonly ConcurrentDictionary<string, long> sequences = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, StringBuilder> streaming = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, DateTimeOffset> lastPush = new(StringComparer.Ordinal);
    readonly HashSet<string> shown = new(StringComparer.Ordinal);
    readonly CancellationTokenSource cts = new();
    readonly Timer typingTimer;
    readonly DateTimeOffset createdAt = DateTimeOffset.Now;
    string? streamingId;
    bool isTyping;


    public AiChatSession(IAiSession session, AiChatSettings settings, IAiAttachmentStore? attachments)
    {
        this.session = session;
        this.settings = settings;
        this.attachments = attachments;
        this.typingTimer = new Timer(_ => this.RaiseTyping(true), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        this.settings.Changed += this.OnSettingsChanged;
        _ = this.Listen(this.cts.Token);
    }


    public IAiSession Session => this.session;
    public string CurrentUserId => AiChatSettings.UserId;

    /// <summary>
    /// Raised when the context gets full enough that the UI should warn (D11).
    /// </summary>
    public event EventHandler<ContextSnapshot>? ContextPressureChanged;

    public ChatSessionInfo Info => new(
        SessionId: this.session.SessionId,
        SessionName: this.session.Title ?? this.settings.BotName,
        Users:
        [
            new ChatSessionUserInfo(AiChatSettings.UserId, this.settings.UserName, this.settings.UserAvatar, this.settings.UserBubbleColor, this.createdAt),
            new ChatSessionUserInfo(AiChatSettings.BotId, this.settings.BotName, this.settings.BotAvatar, this.settings.BotBubbleColor, this.createdAt)
        ],
        PermittedEmojis: [],
        BodyPermissions: MessageBodyPermissions.None,
        Permissions: this.settings.AllowImages
            ? ChatSessionPermissions.CanSendMessages | ChatSessionPermissions.CanSendImages
            : ChatSessionPermissions.CanSendMessages,
        CreatedAt: this.createdAt,
        LastReadDate: DateTimeOffset.Now,
        UnreadMessageCount: 0
    );

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<MessageChanged>? MessageUpdated;
    public event EventHandler<UserTypingEvent>? UserTyping;
    public event EventHandler<ChatSessionInfo>? SessionUpdated;

#pragma warning disable CS0067   // an AI chat never deletes, joins/leaves or goes offline
    public event EventHandler<string>? MessageDeleted;
    public event EventHandler<ChatSessionUserInfo>? UserJoined;
    public event EventHandler<ChatSessionUserInfo>? UserLeft;
    public event EventHandler<ChatConnectionState>? ConnectionStateChanged;
#pragma warning restore CS0067


    // ── history ─────────────────────────────────────────────────────────────

    public async Task<ChatPage> GetMessagesAsync(string? cursorMessageId, MessagePageDirection direction, int count, CancellationToken cancellationToken = default)
    {
        if (direction == MessagePageDirection.Newer)
            return new ChatPage([], false);

        var initial = cursorMessageId is null;
        if (!this.settings.LoadHistory)
            return new ChatPage(this.Greeting(initial), false);

        long? before = cursorMessageId != null && this.sequences.TryGetValue(cursorMessageId, out var seq) ? seq : null;
        var page = await this.session.GetMessages(before, count, cancellationToken).ConfigureAwait(false);

        var mapped = page.Messages.Select(this.Map).OfType<ChatMessage>().ToList();
        if (initial)
            mapped = this.WithPendingChoices(mapped);

        if (mapped.Count == 0 && initial)
            return new ChatPage(this.Greeting(true), false);

        return new ChatPage(mapped, page.HasMore);
    }


    // ── sending ─────────────────────────────────────────────────────────────

    public async Task<ChatMessage> SendMessageAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        var input = AiInput.Text(message.Body ?? String.Empty);
        if (message.Attachment is { } attachment)
        {
            await using var content = attachment.Content;
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            input = input.WithImage(buffer.ToArray(), attachment.ContentType ?? "image/jpeg", retention: this.settings.ImageRetention);
        }

        // the stored user message id becomes the bubble id, so the session's own MessageAdded merges into it
        var turn = this.session.Send(input, CancellationToken.None);
        await foreach (var e in turn.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (e is TurnStarted started)
                return this.Map(started.UserMessage, message.ClientMessageId) ?? this.Placeholder(message);
        }
        return this.Placeholder(message);
    }


    /// <summary>
    /// Shows an informational AI bubble that isn't part of the stored conversation.
    /// </summary>
    internal void PushNotice(string text, string identifier)
        => this.Push(this.Create(AiChatSettings.BotId, text, Guid.NewGuid().ToString("N"), identifier: identifier));


    /// <summary>
    /// Sends a tapped choice as the answer.
    /// </summary>
    internal Task SendAnswerAsync(AiQuestion question, IReadOnlyList<string> choiceIds)
    {
        this.session.Send(AiInput.Answer(question, [.. choiceIds]));
        return Task.CompletedTask;
    }


    // ── session events ──────────────────────────────────────────────────────

    async Task Listen(CancellationToken ct)
    {
        try
        {
            await foreach (var e in this.session.GetEvents(ct).ConfigureAwait(false))
            {
                try
                {
                    this.OnEvent(e);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"AiChatSession: {ex}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AiChatSession: event stream ended: {ex.Message}");
        }
    }


    void OnEvent(AiSessionEvent e)
    {
        switch (e)
        {
            case Snapshot s:
                this.SetTyping(s.State is AiSessionState.Thinking or AiSessionState.Responding);
                break;

            case MessageAdded { Message.Role: MessageRole.Assistant, Message.Status: AiConversation.MessageStatus.Streaming } m:
                this.streamingId = m.Message.MessageId;
                this.streaming[m.Message.MessageId] = new StringBuilder();
                this.sequences[m.Message.MessageId] = m.Message.Sequence;
                break;

            case MessageAdded m:
                if (this.Map(m.Message) is { } added)
                    this.Push(added);
                break;

            case TurnEventOccurred { Event: TextDelta delta }:
                this.OnDelta(delta.Text);
                break;

            case MessageUpdated m when m.Message.Role == MessageRole.Assistant:
                this.OnReplyUpdated(m.Message);
                break;

            case StateChanged s:
                this.SetTyping(s.State is AiSessionState.Thinking or AiSessionState.Responding);
                break;

            case TitleChanged:
                this.SessionUpdated?.Invoke(this, this.Info);
                break;

            case ContextPressureChanged p:
                this.ContextPressureChanged?.Invoke(this, p.Context);
                break;

            case SessionError err:
                this.SetTyping(false);
                this.Push(this.Create(AiChatSettings.BotId, this.settings.ErrorPrefix + err.Error.Message, Guid.NewGuid().ToString("N"), identifier: "error"));
                break;
        }
    }


    void OnDelta(string text)
    {
        if (this.streamingId is not { } id || !this.streaming.TryGetValue(id, out var sb))
            return;

        string body;
        lock (sb)
        {
            sb.Append(text);
            body = sb.ToString();
        }

        var now = DateTimeOffset.UtcNow;
        var first = !this.shown.Contains(id);
        if (!first && this.lastPush.TryGetValue(id, out var last) && now - last < StreamThrottle)
            return;

        this.lastPush[id] = now;
        var message = this.Create(AiChatSettings.BotId, body, id);
        if (first)
            this.Push(message);
        else
            this.MessageUpdated?.Invoke(this, new MessageChanged(message, MessageChangeKind.Edited));
    }


    void OnReplyUpdated(AiMessageDocument doc)
    {
        if (doc.Status == AiConversation.MessageStatus.Streaming)
            return;

        this.streaming.TryRemove(doc.MessageId, out _);
        if (this.streamingId == doc.MessageId)
            this.streamingId = null;

        if (this.Map(doc) is not { } message)
            return;

        // choices belong to the latest reply only; each turn replaces them (L6)
        if (doc.Status == AiConversation.MessageStatus.Complete)
            message = this.WithChoices(message, this.session.PendingQuestions);

        if (this.shown.Contains(doc.MessageId))
            this.MessageUpdated?.Invoke(this, new MessageChanged(message, MessageChangeKind.Edited));
        else
            this.Push(message);
    }


    void OnSettingsChanged(object? sender, EventArgs args) => this.SessionUpdated?.Invoke(this, this.Info);


    // ── typing ──────────────────────────────────────────────────────────────

    void SetTyping(bool typing)
    {
        if (this.isTyping == typing)
            return;

        this.isTyping = typing;
        this.RaiseTyping(typing);
        this.typingTimer.Change(typing ? TypingHeartbeat : Timeout.InfiniteTimeSpan, typing ? TypingHeartbeat : Timeout.InfiniteTimeSpan);
    }

    void RaiseTyping(bool typing)
        => this.UserTyping?.Invoke(this, new UserTypingEvent(AiChatSettings.BotId, typing, DateTimeOffset.UtcNow));


    // ── mapping ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Only what a person should see: user text/answers/images and assistant text. Tool calls, memory writes,
    /// summaries and empty replies have no bubble.
    /// </summary>
    ChatMessage? Map(AiMessageDocument doc) => this.Map(doc, null);

    ChatMessage? Map(AiMessageDocument doc, string? clientMessageId)
    {
        if (doc.Role is not (MessageRole.User or MessageRole.Assistant))
            return null;

        this.sequences[doc.MessageId] = doc.Sequence;
        var image = doc.Parts.OfType<ImagePart>().FirstOrDefault();
        var imageUrl = image != null ? this.attachments?.GetDisplayUri(image.Attachment.AttachmentId) : null;

        var body = doc.Status == AiConversation.MessageStatus.Interrupted && doc.SpokenText != null && this.settings.ShowSpokenPartOfInterruptedReplies
            ? doc.SpokenText + " …"
            : doc.Text;

        if (String.IsNullOrWhiteSpace(body) && imageUrl == null)
            return null;

        if (doc.Role == MessageRole.Assistant && this.settings.ShowTokenUsage && doc.Usage is { } usage)
            body = AiChatTokens.AppendTokenFooter(body ?? String.Empty, usage.InputTokens, usage.OutputTokens, usage.TotalTokens);

        var identifier = doc.Status switch
        {
            AiConversation.MessageStatus.Failed => "failed",
            AiConversation.MessageStatus.Interrupted => "interrupted",
            _ => null
        };
        return this.Create(doc.Role == MessageRole.User ? AiChatSettings.UserId : AiChatSettings.BotId, body, doc.MessageId, clientMessageId, identifier, imageUrl: imageUrl, timestamp: doc.Timestamp.ToLocalTime());
    }


    List<ChatMessage> WithPendingChoices(List<ChatMessage> page)
    {
        var questions = this.session.PendingQuestions;
        if (questions.Count == 0)
            return page;

        var lastBot = page.FindLastIndex(x => x.SenderId == AiChatSettings.BotId);
        if (lastBot >= 0)
            page[lastBot] = this.WithChoices(page[lastBot], questions);

        return page;
    }


    ChatMessage WithChoices(ChatMessage message, IReadOnlyList<AiQuestion> questions)
    {
        if (!this.settings.ShowChoiceButtons)
            return message;

        var withChoices = questions.Where(x => x.HasChoices).ToList();
        if (withChoices.Count == 0)
            return message;

        return message with
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AiChoiceTemplateSelector.QuestionsMetadataKey] = JsonSerializer.Serialize(withChoices, AiConversationJsonContext.Default.ListAiQuestion)
            }
        };
    }


    ChatMessage Create(
        string senderId,
        string? body,
        string messageId,
        string? clientMessageId = null,
        string? identifier = null,
        string? imageUrl = null,
        DateTimeOffset? timestamp = null
    ) => new(
        MessageId: messageId,
        ClientMessageId: clientMessageId,
        SenderId: senderId,
        Body: body,
        ImageUrl: imageUrl,
        Status: ChatMessageStatus.Sent,
        StatusReason: null,
        Timestamp: timestamp ?? DateTimeOffset.Now,
        EditedTimestamp: null,
        Reactions: [],
        ReadReceipts: [],
        Identifier: identifier,
        Metadata: null
    );


    ChatMessage Placeholder(OutgoingMessage message)
        => this.Create(AiChatSettings.UserId, message.Body, Guid.NewGuid().ToString("N"), message.ClientMessageId);


    void Push(ChatMessage message)
    {
        this.shown.Add(message.MessageId);
        this.MessageReceived?.Invoke(this, message);   // the control marshals to the UI thread
    }


    IReadOnlyList<ChatMessage> Greeting(bool initialPage)
    {
        if (!initialPage || String.IsNullOrWhiteSpace(this.settings.GreetingMessage))
            return [];

        return [this.Create(AiChatSettings.BotId, this.settings.GreetingMessage, "greeting", identifier: "greeting")];
    }


    // ── not applicable to an AI chat (permissions gate these off) ───────────

    public Task<ChatMessage> ResendMessageAsync(string clientMessageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task EditMessageAsync(string messageId, string body, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteMessageAsync(string messageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReactToMessageAsync(string messageId, string emoji, bool add, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task MarkReadAsync(string[] messageIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ToggleTypingAsync(bool isTyping, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task InviteUserAsync(string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task LeaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RenameAsync(string sessionName, CancellationToken cancellationToken = default) => this.session.SetTitle(sessionName, cancellationToken);


    public ValueTask DisposeAsync()
    {
        // the chat doesn't own the IAiSession: whoever handed it over disposes it
        this.cts.Cancel();
        this.settings.Changed -= this.OnSettingsChanged;
        this.typingTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}
