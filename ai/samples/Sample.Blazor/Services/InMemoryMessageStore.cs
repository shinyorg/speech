using Microsoft.Extensions.AI;
using Shiny.AiConversation;

namespace Sample.Blazor.Services;

public class InMemoryMessageStore : IMessageStore
{
    readonly List<AiChatMessage> messages = [];
    readonly object sync = new();

    public Task Store(string? userTriggeringMessage, ChatResponse response, CancellationToken cancellationToken)
    {
        if (response.Text is not { } text)
            return Task.CompletedTask;

        lock (sync)
        {
            messages.Add(new AiChatMessage(
                Guid.NewGuid().ToString(),
                text,
                DateTimeOffset.UtcNow,
                ChatMessageDirection.AI
            ));
        }
        return Task.CompletedTask;
    }

    public Task Clear(DateTimeOffset? beforeDate = null)
    {
        lock (sync)
        {
            if (beforeDate.HasValue)
                messages.RemoveAll(m => m.Timestamp <= beforeDate.Value);
            else
                messages.Clear();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AiChatMessage>> Query(
        string? messageContains = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            IEnumerable<AiChatMessage> query = messages.OrderBy(m => m.Timestamp);

            if (!String.IsNullOrWhiteSpace(messageContains))
                query = query.Where(m => m.Message.Contains(messageContains, StringComparison.OrdinalIgnoreCase));

            if (fromDate.HasValue)
                query = query.Where(m => m.Timestamp >= fromDate.Value);

            if (toDate.HasValue)
                query = query.Where(m => m.Timestamp <= toDate.Value);

            if (limit.HasValue)
                query = query.Take(limit.Value);

            return Task.FromResult<IReadOnlyList<AiChatMessage>>(query.ToList().AsReadOnly());
        }
    }
}
