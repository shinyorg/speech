using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Shiny.AiConversation.Infrastructure;

public class ChatLookupAITool(IMessageStore messageStore)
{
    public AITool AsTool() => AIFunctionFactory.Create(
        this.LookupChatHistory,
        "lookup_chat_history",
        "Searches previous chat history for messages matching a query. Use this when the user asks about past conversations or wants to recall something they previously discussed."
    );

    [Description("Searches previous chat history for messages matching a query")]
    async Task<string> LookupChatHistory(
        [Description("Text to search for in past messages")] string? query = null,
        [Description("Start date filter in ISO 8601 format")] string? fromDate = null,
        [Description("End date filter in ISO 8601 format")] string? toDate = null,
        CancellationToken cancellationToken = default
    )
    {
        DateTimeOffset? from = fromDate != null ? DateTimeOffset.Parse(fromDate) : null;
        DateTimeOffset? to = toDate != null ? DateTimeOffset.Parse(toDate) : null;

        var messages = await messageStore.Query(query, from, to, limit: 50, cancellationToken: cancellationToken);

        if (messages.Count == 0)
            return "No matching messages found in chat history.";

        var results = messages.Select(m =>
            $"[{m.Timestamp:yyyy-MM-dd HH:mm}] {m.Direction}: {m.Message}"
        );

        return String.Join("\n", results);
    }
}
