using System.Text;
using Microsoft.Extensions.AI;

namespace Shiny.AiConversation.Infrastructure;

/// <summary>
/// The default compactor: asks the session's own model to fold the previous summary and the oldest
/// messages into one rolling summary.
/// </summary>
public class ChatClientContextCompactor(IChatClientProvider chatClientProvider) : IContextCompactor
{
    const string Instructions =
        "You maintain the running summary of a conversation between a user and an AI assistant. " +
        "Rewrite the summary so it also covers the new messages. Keep everything the assistant will be expected to remember: " +
        "names, preferences, facts, decisions, open tasks and unanswered questions. Drop small talk. " +
        "Write plain prose in the third person, under 300 words. Reply with the summary only.";

    public async Task<string> Summarize(string? previousSummary, IReadOnlyList<AiMessageDocument> messages, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (!String.IsNullOrWhiteSpace(previousSummary))
        {
            sb.AppendLine("<previous_summary>");
            sb.AppendLine(previousSummary);
            sb.AppendLine("</previous_summary>");
        }

        sb.AppendLine("<messages>");
        foreach (var msg in messages)
        {
            var text = msg.Status == MessageStatus.Interrupted ? msg.SpokenText ?? msg.Text : msg.Text;
            if (String.IsNullOrWhiteSpace(text) || msg.Role is not (MessageRole.User or MessageRole.Assistant))
                continue;

            sb.Append(msg.Role == MessageRole.User ? "User: " : "Assistant: ");
            sb.AppendLine(text);
        }
        sb.Append("</messages>");

        var client = await chatClientProvider.GetChatClient(ct).ConfigureAwait(false);
        var response = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, Instructions),
                new ChatMessage(ChatRole.User, sb.ToString())
            ],
            cancellationToken: ct
        ).ConfigureAwait(false);

        return response.Text.Trim();
    }
}
