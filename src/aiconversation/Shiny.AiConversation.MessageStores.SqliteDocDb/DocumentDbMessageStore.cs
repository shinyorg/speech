using Microsoft.Extensions.AI;
using Shiny.DocumentDb;

namespace Shiny.AiConversation.MessageStores.SqliteDocDb;


public class DocumentDbMessageStore(IDocumentStore store) : IMessageStore
{
    public async Task Store(string? userTriggeringMessage, ChatResponse response, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!String.IsNullOrWhiteSpace(userTriggeringMessage))
        {
            await store.Insert(
                new AiChatMessage(
                    Guid.NewGuid().ToString(),
                    userTriggeringMessage,
                    now,
                    ChatMessageDirection.User
                ),
                AppJsonContext.Default.AiChatMessage,
                cancellationToken
            ).ConfigureAwait(false);
        }

        if (response.Text is { } text && !String.IsNullOrWhiteSpace(text))
        {
            var usage = response.Usage;
            await store.Insert(
                new AiChatMessage(
                    Guid.NewGuid().ToString(),
                    text,
                    // Bump one tick so this row sorts strictly after the user message
                    // even when the system clock returns identical timestamps.
                    now.AddTicks(1),
                    ChatMessageDirection.AI,
                    usage?.InputTokenCount,
                    usage?.OutputTokenCount,
                    usage?.TotalTokenCount
                ),
                AppJsonContext.Default.AiChatMessage,
                cancellationToken
            ).ConfigureAwait(false);
        }
    }

    public Task Clear(DateTimeOffset? beforeDate = null)
    {
        var query = store.Query(AppJsonContext.Default.AiChatMessage);

        if (beforeDate.HasValue)
            query = query.Where(x => x.Timestamp <= beforeDate.Value);

        return query.ExecuteDelete();
    }

    public async Task<IReadOnlyList<AiChatMessage>> Query(
        string? messageContains = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int? limit = null,
        CancellationToken cancellationToken = default
    )
    {
        // When a limit is supplied, return the LATEST N matching messages
        // (then reverse to chronological order for display). Without it,
        // return everything ordered chronologically.
        var query = limit.HasValue
            ? store.Query(AppJsonContext.Default.AiChatMessage).OrderByDescending(x => x.Timestamp)
            : store.Query(AppJsonContext.Default.AiChatMessage).OrderBy(x => x.Timestamp);

        if (!String.IsNullOrWhiteSpace(messageContains))
            query = query.Where(x => x.Message.Contains(messageContains));

        if (fromDate.HasValue)
            query = query.Where(x => x.Timestamp >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(x => x.Timestamp <= toDate.Value);

        if (limit.HasValue)
        {
            query = query.Paginate(0, limit.Value);
            var page = await query.ToList(cancellationToken).ConfigureAwait(false);
            return page.AsEnumerable().Reverse().ToList();
        }

        return await query.ToList(cancellationToken).ConfigureAwait(false);
    }
}
