using System.Collections.Concurrent;
using System.Globalization;
using Shiny.AiConversation;
using Shiny.Maui.Controls.Chat;

namespace MauiSample.Pages;

/// <summary>
/// Bridges the new provider-driven <see cref="ChatView"/> onto <see cref="IAiConversationService"/>.
/// There is a single, global AI conversation (backed by the message store), so this exposes one
/// logical session. It is <c>new</c>'d up by <see cref="ChatViewModel"/> — no DI registration needed.
/// </summary>
public class AiConversationChatSessionProvider(IAiConversationService ai) : IChatSessionProvider
{
    public const string DefaultSessionId = "ai";

    public Task<IChatSession> CreateSessionAsync(string[] userIds, CancellationToken cancellationToken = default)
        => Task.FromResult<IChatSession>(new AiConversationChatSession(ai));

    public Task<IChatSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IChatSession>(new AiConversationChatSession(ai));
}


/// <summary>
/// A live session over the AI conversation. User sends are forwarded to <see cref="IAiConversationService.TalkTo"/>;
/// AI replies arrive via <see cref="IAiConversationService.AiResponded"/> and are pushed to the control as
/// received messages. History paging maps onto <see cref="IAiConversationService.GetChatHistory"/>.
/// </summary>
sealed class AiConversationChatSession : IChatSession
{
    const string MeId = "me";
    const string AiId = "ai";

    readonly IAiConversationService ai;
    readonly ChatSessionUserInfo[] users;
    readonly DateTimeOffset createdAt = DateTimeOffset.Now;

    // Maps our message ids to their timestamps so cursor-based paging can resolve an "older than" bound.
    readonly ConcurrentDictionary<string, DateTimeOffset> timestamps = new(StringComparer.Ordinal);

    public AiConversationChatSession(IAiConversationService ai)
    {
        this.ai = ai;
        this.users =
        [
            new ChatSessionUserInfo(MeId, "Me", null, null, this.createdAt),
            new ChatSessionUserInfo(
                AiId,
                "Copilot",
                new FontImageSource { Glyph = "\U0001F916", FontFamily = "OpenSansRegular", Size = 24, Color = Colors.White },
                Color.FromArgb("#5865F2"),
                this.createdAt)
        ];
        this.ai.AiResponded += this.OnAiResponded;
    }

    public string CurrentUserId => MeId;

    public ChatSessionInfo Info => new(
        SessionId: AiConversationChatSessionProvider.DefaultSessionId,
        SessionName: "Copilot",
        Users: this.users,
        PermittedEmojis: Array.Empty<string>(),          // an AI chat has no reactions
        BodyPermissions: MessageBodyPermissions.None,    // plain-text input (AI replies still render markdown)
        Permissions: ChatSessionPermissions.CanSendMessages,
        CreatedAt: this.createdAt,
        LastReadDate: DateTimeOffset.Now,
        UnreadMessageCount: 0
    );

    public event EventHandler<ChatMessage>? MessageReceived;

    // An AI chat only ever pushes received messages; the remaining session events are part of the
    // interface but intentionally never raised here.
#pragma warning disable CS0067
    public event EventHandler<MessageChanged>? MessageUpdated;
    public event EventHandler<string>? MessageDeleted;
    public event EventHandler<UserTypingEvent>? UserTyping;
    public event EventHandler<ChatSessionUserInfo>? UserJoined;
    public event EventHandler<ChatSessionUserInfo>? UserLeft;
    public event EventHandler<ChatSessionInfo>? SessionUpdated;
    public event EventHandler<ChatConnectionState>? ConnectionStateChanged;
#pragma warning restore CS0067


    // ---- history paging (cursor-based, older-only) ----

    public async Task<MessagePage> GetMessagesAsync(
        string? cursorMessageId,
        MessagePageDirection direction,
        int count,
        CancellationToken cancellationToken = default)
    {
        if (direction == MessagePageDirection.Newer)
            return new MessagePage(Array.Empty<ChatMessage>(), false);

        DateTimeOffset? endDate = null;
        if (cursorMessageId is not null && this.timestamps.TryGetValue(cursorMessageId, out var ts))
            endDate = ts.AddTicks(-1); // strictly older than the cursor

        var history = await this.ai
            .GetChatHistory(endDate: endDate, limit: count)
            .ConfigureAwait(false);

        var mapped = history.Select(this.Map).ToList(); // GetChatHistory returns ascending by timestamp
        return new MessagePage(mapped, history.Count >= count);
    }


    // ---- outgoing ----

    public Task<ChatMessage> SendMessageAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        // We don't advertise CanSendImages, but the provider owns any attachment stream regardless.
        message.Attachment?.Content.Dispose();

        var now = DateTimeOffset.Now;
        var id = Guid.NewGuid().ToString("N");
        this.timestamps[id] = now;

        var sent = new ChatMessage(
            MessageId: id,
            ClientMessageId: string.IsNullOrEmpty(message.ClientMessageId) ? null : message.ClientMessageId,
            SenderId: MeId,
            Body: message.Body,
            ImageUrl: null,
            Status: MessageStatus.Sent,
            StatusReason: null,
            Timestamp: now,
            EditedTimestamp: null,
            Reactions: Array.Empty<Reaction>(),
            ReadReceipts: Array.Empty<ReadReceipt>()
        );

        // Fire the AI round-trip in the background; the reply surfaces via AiResponded -> MessageReceived.
        _ = this.TalkAsync(message.Body ?? String.Empty);
        return Task.FromResult(sent);
    }

    async Task TalkAsync(string text)
    {
        try
        {
            await this.ai.TalkTo(text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.RaiseAiMessage("⚠️ " + ex.Message, null, null, null);
        }
    }

    void OnAiResponded(AiResponse response)
    {
        if (response.Response.Text is not { } text)
            return;

        var usage = response.Response.Usage;
        this.RaiseAiMessage(text, usage?.InputTokenCount, usage?.OutputTokenCount, usage?.TotalTokenCount);
    }

    void RaiseAiMessage(string text, long? inputTokens, long? outputTokens, long? totalTokens)
    {
        var now = DateTimeOffset.Now;
        var id = Guid.NewGuid().ToString("N");
        this.timestamps[id] = now;

        var msg = new ChatMessage(
            MessageId: id,
            ClientMessageId: null,
            SenderId: AiId,
            Body: AiChatTokens.AppendTokenFooter(text, inputTokens, outputTokens, totalTokens),
            ImageUrl: null,
            Status: MessageStatus.Sent,
            StatusReason: null,
            Timestamp: now,
            EditedTimestamp: null,
            Reactions: Array.Empty<Reaction>(),
            ReadReceipts: Array.Empty<ReadReceipt>()
        );
        this.MessageReceived?.Invoke(this, msg); // control marshals to the UI thread
    }

    ChatMessage Map(AiChatMessage m)
    {
        this.timestamps[m.Id] = m.Timestamp;

        var body = m.Direction == ChatMessageDirection.AI
            ? AiChatTokens.AppendTokenFooter(m.Message, m.InputTokens, m.OutputTokens, m.TotalTokens)
            : m.Message;

        return new ChatMessage(
            MessageId: m.Id,
            ClientMessageId: null,
            SenderId: m.Direction == ChatMessageDirection.User ? MeId : AiId,
            Body: body,
            ImageUrl: null,
            Status: MessageStatus.Read,
            StatusReason: null,
            Timestamp: m.Timestamp,
            EditedTimestamp: null,
            Reactions: Array.Empty<Reaction>(),
            ReadReceipts: Array.Empty<ReadReceipt>()
        );
    }


    // ---- unsupported for an AI chat (permissions gate these off, so the control never calls them) ----

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
        return ValueTask.CompletedTask;
    }
}


/// <summary>Formats the per-message token-usage footer shared by the chat and Aura screens.</summary>
static class AiChatTokens
{
    public static string AppendTokenFooter(string body, long? input, long? output, long? total)
    {
        var footer = FormatTokenFooter(input, output, total);
        return footer is null ? body : body + "\n\n— " + footer;
    }

    public static string? FormatTokenFooter(long? input, long? output, long? total)
    {
        if (total is null && input is null && output is null)
            return null;

        var ci = CultureInfo.InvariantCulture;
        var totalStr = (total ?? ((input ?? 0) + (output ?? 0))).ToString("N0", ci);

        if (input.HasValue && output.HasValue)
            return $"{totalStr} tokens ({input.Value.ToString("N0", ci)} in · {output.Value.ToString("N0", ci)} out)";

        return $"{totalStr} tokens";
    }
}
