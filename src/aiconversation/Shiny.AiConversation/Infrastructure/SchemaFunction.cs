using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Shiny.AiConversation.Infrastructure;

/// <summary>
/// A tool with a hand-written JSON schema and a delegate body. Keeps tools AOT/trim safe (no reflection
/// over delegate parameters). Used by the built-in tools; available to apps and add-on packages.
/// </summary>
public sealed class SchemaFunction : AIFunction
{
    readonly string name;
    readonly string description;
    readonly JsonElement schema;
    readonly Func<AIFunctionArguments, CancellationToken, Task<object?>> invoke;


    public SchemaFunction(string name, string description, string jsonSchema, Func<AIFunctionArguments, CancellationToken, Task<string>> invoke)
        : this(name, description, jsonSchema, async (a, ct) => (object?)await invoke(a, ct).ConfigureAwait(false)) { }


    /// <param name="invoke">May return AI content (e.g. text plus a <see cref="DataContent"/> image) as well as a string.</param>
    public SchemaFunction(string name, string description, string jsonSchema, Func<AIFunctionArguments, CancellationToken, Task<object?>> invoke)
    {
        this.name = name;
        this.description = description;
        this.schema = JsonDocument.Parse(jsonSchema).RootElement.Clone();
        this.invoke = invoke;
    }


    public override string Name => this.name;
    public override string Description => this.description;
    public override JsonElement JsonSchema => this.schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => await this.invoke(arguments, cancellationToken).ConfigureAwait(false);
}


/// <summary>
/// Reads tool arguments whether the provider supplied JSON elements or plain values.
/// </summary>
public static class ToolArguments
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
