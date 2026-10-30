using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Shiny.AiConversation.Infrastructure;

/// <summary>
/// The one place stored messages are mapped to and from Microsoft.Extensions.AI types.
/// </summary>
static class MessageMapper
{
    public const string InterruptedMarker = "[interrupted by user]";
    public const string SummaryPrefix = "Summary of the earlier conversation:";

    /// <summary>
    /// Stored history to prompt messages. Skips compacted, failed, still-streaming and summary messages, sends
    /// interrupted replies as what was actually heard, and drops tool calls whose result never arrived
    /// (providers reject an unanswered tool call).
    /// </summary>
    /// <param name="images">Image bytes to include, by attachment id; any other image is mentioned as text.</param>
    public static List<ChatMessage> ToChatMessages(IEnumerable<AiMessageDocument> messages, IReadOnlyDictionary<string, DataContent>? images = null)
    {
        var result = new List<ChatMessage>();
        foreach (var msg in messages)
        {
            if (msg.Compacted || msg.Status is MessageStatus.Streaming or MessageStatus.Failed)
                continue;

            switch (msg.Role)
            {
                case MessageRole.User:
                    var userContents = new List<AIContent>();
                    foreach (var part in msg.Parts)
                    {
                        switch (part)
                        {
                            case TextPart { Text.Length: > 0 } t:
                                userContents.Add(new TextContent(t.Text));
                                break;

                            case AnswerPart { Text.Length: > 0 } a:
                                userContents.Add(new TextContent(a.Text));
                                break;

                            case ImagePart i when images != null && images.TryGetValue(i.Attachment.AttachmentId, out var data):
                                userContents.Add(data);
                                break;

                            case ImagePart i:
                                userContents.Add(new TextContent($"[image: {i.Description ?? "photo"}]"));
                                break;
                        }
                    }

                    if (userContents.Count > 0)
                        result.Add(new ChatMessage(ChatRole.User, userContents));
                    break;

                case MessageRole.System:
                    if (!String.IsNullOrWhiteSpace(msg.Text))
                        result.Add(new ChatMessage(ChatRole.System, msg.Text));
                    break;

                case MessageRole.Summary:
                    // the current summary goes at the top of the prompt from the session header; summary
                    // messages are history markers for the UI
                    break;

                case MessageRole.Assistant:
                case MessageRole.Tool:
                    AddModelMessages(result, msg);
                    break;
            }
        }
        return result;
    }

    static void AddModelMessages(List<ChatMessage> result, AiMessageDocument msg)
    {
        var parts = msg.Parts.AsEnumerable();
        if (msg.Status == MessageStatus.Interrupted)
        {
            // the model is told what was heard, not what it generated
            var heard = msg.SpokenText ?? GetText(msg.Parts);
            parts = parts.Where(x => x is not TextPart);
            if (!String.IsNullOrWhiteSpace(heard))
                parts = parts.Append(new TextPart($"{heard.TrimEnd()} {InterruptedMarker}"));
        }

        var list = parts.ToList();
        var calls = list.OfType<ToolCallPart>().Select(x => x.CallId).ToHashSet();
        var results = list.OfType<ToolResultPart>().Select(x => x.CallId).ToHashSet();

        var assistant = new List<AIContent>();
        var tool = new List<AIContent>();

        foreach (var part in list)
        {
            switch (part)
            {
                case TextPart t when !String.IsNullOrEmpty(t.Text):
                    Flush(result, ChatRole.Tool, tool);
                    assistant.Add(new TextContent(t.Text));
                    break;

                case ToolCallPart c when results.Contains(c.CallId):
                    Flush(result, ChatRole.Tool, tool);
                    assistant.Add(new FunctionCallContent(c.CallId, c.Name, ParseArguments(c.ArgumentsJson)));
                    break;

                case ToolResultPart r when calls.Contains(r.CallId):
                    Flush(result, ChatRole.Assistant, assistant);
                    tool.Add(new FunctionResultContent(r.CallId, r.Result));
                    break;
            }
        }
        Flush(result, ChatRole.Assistant, assistant);
        Flush(result, ChatRole.Tool, tool);
    }

    static void Flush(List<ChatMessage> result, ChatRole role, List<AIContent> contents)
    {
        if (contents.Count == 0)
            return;

        result.Add(new ChatMessage(role, contents.ToList()));
        contents.Clear();
    }

    /// <summary>
    /// Model response messages (from <c>ToChatResponse()</c>) to stored parts, in order.
    /// </summary>
    public static List<MessagePart> ToParts(IEnumerable<ChatMessage> messages)
    {
        var parts = new List<MessagePart>();
        var callNames = new Dictionary<string, string>();

        foreach (var msg in messages)
        {
            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextContent t when !String.IsNullOrEmpty(t.Text):
                        if (parts.Count > 0 && parts[^1] is TextPart last && msg.Role == ChatRole.Assistant)
                            parts[^1] = last with { Text = last.Text + t.Text };
                        else
                            parts.Add(new TextPart(t.Text));
                        break;

                    case FunctionCallContent f:
                        callNames[f.CallId] = f.Name;
                        parts.Add(new ToolCallPart(f.CallId, f.Name, SerializeArguments(f.Arguments)));
                        break;

                    case FunctionResultContent r:
                        parts.Add(new ToolResultPart(
                            r.CallId,
                            callNames.GetValueOrDefault(r.CallId) ?? String.Empty,
                            ResultToString(r.Result),
                            r.Exception != null
                        ));
                        break;
                }
            }
        }
        return parts;
    }

    /// <summary>
    /// The text a person would see: text and answer parts, separated by blank lines.
    /// </summary>
    public static string? GetText(IEnumerable<MessagePart> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            var text = part switch
            {
                TextPart t when t.Audience.HasFlag(Audience.Displayed) || t.Audience.HasFlag(Audience.Spoken) => t.Text,
                AnswerPart a => a.Text,
                _ => null
            };
            if (String.IsNullOrEmpty(text))
                continue;

            if (sb.Length > 0)
                sb.Append("\n\n");

            sb.Append(text);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    internal static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments == null || arguments.Count == 0)
            return "{}";

        return JsonSerializer.Serialize(
            arguments,
            AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>))
        );
    }

    static Dictionary<string, object?>? ParseArguments(string json)
    {
        if (String.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var dict = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();

            return dict;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string ResultToString(object? result) => result switch
    {
        null => String.Empty,
        string s => s,
        // binary never goes into stored messages
        DataContent d => $"[{d.MediaType} attachment]",
        TextContent t => t.Text,
        IEnumerable<AIContent> contents => String.Join("\n", contents.Select(ResultToString)),
        JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? String.Empty : e.GetRawText(),
        _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)))
    };
}
