using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Shiny.AiConversation.Infrastructure;

/// <summary>
/// A tool with a hand-written JSON schema and a delegate body. Keeps built-in tools AOT/trim safe
/// (no reflection over delegate parameters).
/// </summary>
sealed class SchemaFunction(
    string name,
    string description,
    string jsonSchema,
    Func<AIFunctionArguments, CancellationToken, Task<string>> invoke
) : AIFunction
{
    readonly JsonElement schema = JsonDocument.Parse(jsonSchema).RootElement.Clone();

    public override string Name => name;
    public override string Description => description;
    public override JsonElement JsonSchema => this.schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => await invoke(arguments, cancellationToken).ConfigureAwait(false);
}


static class ToolArguments
{
    public static string? GetString(this IDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value == null)
            return null;

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement e => e.GetRawText(),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    public static int? GetInt(this IDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value == null)
            return null;

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out var i) => i,
            _ => Int32.TryParse(args.GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
        };
    }

    public static DateTimeOffset? GetDate(this IDictionary<string, object?> args, string name)
        => DateTimeOffset.TryParse(args.GetString(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}
