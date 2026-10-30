using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Shiny.AiConversation.Infrastructure;

/// <summary>
/// The built-in <c>ask_user</c> tool. It replaces the v2 JSON reply envelope: the model streams a plain
/// reply and calls this when it needs an answer. The call is terminating, so the turn ends right after it
/// and the session waits for the user. Hand-written schema and source-generated parsing keep it AOT safe.
/// </summary>
sealed class AskUserFunction(bool singleQuestion, Action<IReadOnlyList<AiQuestion>> onAsked) : AIFunction
{
    public const string ToolName = "ask_user";
    public const string Result = "The question is shown to the user. Stop here and wait for their reply.";

    static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "questions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string", "description": "A short stable id for the question, e.g. 'size'." },
                  "text": { "type": "string", "description": "The question." },
                  "choices": {
                    "type": "array",
                    "description": "Only when there is a small fixed set of valid answers.",
                    "items": {
                      "type": "object",
                      "properties": {
                        "id": { "type": "string" },
                        "label": { "type": "string", "description": "Short text shown to the user." }
                      },
                      "required": ["id", "label"]
                    }
                  },
                  "allowMultiple": { "type": "boolean", "description": "True when several choices may be selected." }
                },
                "required": ["id", "text"]
              }
            }
          },
          "required": ["questions"]
        }
        """
    ).RootElement.Clone();

    public override string Name => ToolName;
    public override JsonElement JsonSchema => Schema;

    const string Common =
        "Ask the user something you need before you can continue. Always ask the question in your reply text too: " +
        "this tool only shows answer choices and keeps the conversation open for the reply. Calling it ends your turn. " +
        "For a yes/no question give the choices {id: yes, label: Yes} and {id: no, label: No}. ";

    public override string Description => singleQuestion
        ? Common + "Your reply is spoken aloud, so ask at most one question."
        : Common + "You may ask several independent questions at once.";

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var questions = Parse(arguments);
        if (singleQuestion && questions.Count > 1)
            questions = [questions[0]];

        onAsked(questions);

        if (FunctionInvokingChatClient.CurrentContext is { } context)
            context.Terminate = true;

        return new ValueTask<object?>(Result);
    }

    internal static IReadOnlyList<AiQuestion> Parse(IDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("questions", out var raw) || raw == null)
            return [];

        try
        {
            var element = raw switch
            {
                JsonElement e => e,
                string s => JsonDocument.Parse(s).RootElement,
                _ => JsonSerializer.SerializeToElement(raw, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)))
            };

            // some models double-encode the array as a string
            if (element.ValueKind == JsonValueKind.String)
                element = JsonDocument.Parse(element.GetString()!).RootElement;

            var items = element.Deserialize(AskUserJsonContext.Default.ListAskUserQuestion) ?? [];
            var result = new List<AiQuestion>();
            for (var i = 0; i < items.Count; i++)
            {
                var q = items[i];
                if (String.IsNullOrWhiteSpace(q.Text))
                    continue;

                var choices = q.Choices?
                    .Where(c => !String.IsNullOrWhiteSpace(c.Label))
                    .Select((c, ci) => new AiChoice(String.IsNullOrWhiteSpace(c.Id) ? $"c{ci + 1}" : c.Id, c.Label!))
                    .ToList();

                result.Add(new AiQuestion(
                    String.IsNullOrWhiteSpace(q.Id) ? $"q{i + 1}" : q.Id,
                    q.Text,
                    choices is { Count: > 0 } ? choices : null,
                    q.AllowMultiple ?? false
                ));
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

record AskUserQuestion(string? Id, string? Text, List<AskUserChoice>? Choices, bool? AllowMultiple);
record AskUserChoice(string? Id, string? Label);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<AskUserQuestion>))]
partial class AskUserJsonContext : JsonSerializerContext;
