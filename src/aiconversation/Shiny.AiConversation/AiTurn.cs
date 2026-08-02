using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shiny.AiConversation;

/// <summary>
/// A single structured turn from the AI. When structured output is enabled (see
/// <see cref="AiStructuredOutputMode"/>) the model is asked to return this shape rather than free text,
/// which gives the conversation a typed "I need something back from you" signal instead of guessing
/// from the wording of the reply.
/// </summary>
/// <param name="Reply">
/// The natural-language answer. This - never the raw JSON - is what gets spoken aloud and rendered
/// in the chat.
/// </param>
/// <param name="Questions">
/// What the AI needs from the user before it can continue. Empty or null means the turn is complete
/// and the conversation can go back to idle (or back to the wake word).
/// </param>
public record AiTurn(
    string Reply,
    AiQuestion[]? Questions = null
)
{
    /// <summary>True when the AI is waiting on the user, i.e. at least one question was returned.</summary>
    [JsonIgnore]
    public bool ExpectsResponse => this.Questions is { Length: > 0 };
}

/// <summary>
/// One thing the AI needs from the user. A turn may carry several, which the conversation service
/// treats as a queue.
/// </summary>
/// <param name="Id">A stable identifier for the question, used to correlate an answer back to it.</param>
/// <param name="Text">The question as it should be shown or spoken.</param>
/// <param name="Choices">
/// The valid answers when there is a small fixed set of them. Null or empty means a free-form answer.
/// </param>
/// <param name="AllowMultiple">True when more than one choice may be selected.</param>
public record AiQuestion(
    string Id,
    string Text,
    AiChoice[]? Choices = null,
    bool AllowMultiple = false
)
{
    /// <summary>True when the question offers a fixed set of answers.</summary>
    [JsonIgnore]
    public bool HasChoices => this.Choices is { Length: > 0 };
}

/// <summary>One selectable answer to an <see cref="AiQuestion"/>.</summary>
/// <param name="Id">A stable identifier for the choice.</param>
/// <param name="Label">Short human text - this is what is sent back as the user's answer when tapped.</param>
public record AiChoice(string Id, string Label);

/// <summary>
/// How the structured turn is requested from the model. Providers differ in what they support, so
/// <see cref="IChatClientProvider.StructuredOutputMode"/> declares the best mode for each one and the
/// conversation service always falls back to plain text when parsing fails.
/// </summary>
public enum AiStructuredOutputMode
{
    /// <summary>
    /// Send a JSON schema on the request (OpenAI's native structured output). The most reliable mode,
    /// but the model and endpoint must support schema-constrained responses.
    /// </summary>
    JsonSchema,

    /// <summary>
    /// Ask for JSON without a schema and describe the shape in the prompt. Use for endpoints that
    /// support a JSON response format but not schemas.
    /// </summary>
    Json,

    /// <summary>
    /// Describe the shape in the prompt only, with no response-format constraint on the request.
    /// The last resort for endpoints that reject both of the above.
    /// </summary>
    Prompt,

    /// <summary>
    /// Don't ask for structured output at all. Replies are plain text and the "expects a response"
    /// signal falls back to inspecting the wording of the reply.
    /// </summary>
    None
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
)]
[JsonSerializable(typeof(AiTurn))]
[JsonSerializable(typeof(AiQuestion[]))]
internal partial class AiTurnJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes the structured turn wire format. Parsing is deliberately forgiving - models wrap
/// JSON in markdown fences or pad it with prose often enough that a strict parse would lose otherwise
/// good replies. Public so apps driving <see cref="Microsoft.Extensions.AI.IChatClient"/> themselves can
/// reuse the same contract.
/// </summary>
public static class AiTurnSerializer
{
    /// <summary>The serializer options - and therefore the JSON schema - used for the turn contract.</summary>
    public static JsonSerializerOptions JsonOptions => AiTurnJsonContext.Default.Options;

    /// <summary>Serializes questions to JSON, for transports that only carry strings.</summary>
    public static string SerializeQuestions(IReadOnlyList<AiQuestion> questions)
        => JsonSerializer.Serialize(questions.ToArray(), AiTurnJsonContext.Default.AiQuestionArray);

    /// <summary>Deserializes questions from JSON, returning null when the payload is unusable.</summary>
    public static AiQuestion[]? DeserializeQuestions(string? json)
    {
        if (String.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize(json, AiTurnJsonContext.Default.AiQuestionArray);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a turn out of the model's raw text, tolerating markdown fences and surrounding prose.
    /// Returns null when the text is not a usable turn, which callers should treat as "plain reply".
    /// </summary>
    public static AiTurn? Parse(string? text)
    {
        if (String.IsNullOrWhiteSpace(text))
            return null;

        var json = ExtractJsonObject(text);
        if (json is null)
            return null;

        try
        {
            var turn = JsonSerializer.Deserialize(json, AiTurnJsonContext.Default.AiTurn);

            // A turn with no reply is useless downstream - there would be nothing to speak or render.
            return String.IsNullOrWhiteSpace(turn?.Reply) ? null : turn;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls the outermost JSON object out of the text, tolerating markdown fences and surrounding
    /// prose. Brace counting is string-aware so a '}' inside the reply text doesn't end the object early.
    /// </summary>
    static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
            return null;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;

                case '{':
                    depth++;
                    break;

                case '}':
                    depth--;
                    if (depth == 0)
                        return text.Substring(start, i - start + 1);
                    break;
            }
        }

        return null;
    }
}
