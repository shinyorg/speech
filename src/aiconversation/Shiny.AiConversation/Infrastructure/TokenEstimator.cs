using Microsoft.Extensions.AI;

namespace Shiny.AiConversation.Infrastructure;

/// <summary>
/// Rough token estimate for when the provider reports no usage: ~4 characters per token plus fixed
/// overhead per message and per tool definition. Only used to size the context, never for billing.
/// </summary>
static class TokenEstimator
{
    const int CharsPerToken = 4;
    const int PerMessageOverhead = 4;
    const int PerToolOverhead = 20;

    public static long Estimate(IEnumerable<ChatMessage> messages, IEnumerable<AITool> tools)
    {
        long chars = 0;
        long overhead = 0;

        foreach (var msg in messages)
        {
            overhead += PerMessageOverhead;
            foreach (var content in msg.Contents)
            {
                chars += content switch
                {
                    TextContent t => t.Text?.Length ?? 0,
                    FunctionCallContent f => f.Name.Length + (f.Arguments?.Sum(a => a.Key.Length + (a.Value?.ToString()?.Length ?? 0)) ?? 0),
                    FunctionResultContent r => r.Result?.ToString()?.Length ?? 0,
                    _ => 0
                };
            }
        }

        foreach (var tool in tools)
        {
            overhead += PerToolOverhead;
            chars += tool.Name.Length + tool.Description.Length;
            if (tool is AIFunctionDeclaration f)
                chars += f.JsonSchema.GetRawText().Length;
        }

        return overhead + (chars + CharsPerToken - 1) / CharsPerToken;
    }
}
