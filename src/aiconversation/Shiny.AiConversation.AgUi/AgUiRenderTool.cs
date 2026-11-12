using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Shiny.AiConversation.AgUi;

/// <summary>
/// The <c>render_ui</c> tool: lets the model draw interactive UI as A2UI v1.0 messages. When a session
/// runs over AG-UI (<see cref="AiSessionAgUiExtensions.RunAgUiAsync"/>) each call becomes an
/// <c>a2ui-surface</c> activity, which an A2UI renderer (the Shiny AgentView) draws; button actions
/// come back to the model as the next turn.
/// </summary>
public sealed class AgUiRenderTool : AIFunction
{
    /// <summary>The tool's name.</summary>
    public const string ToolName = "render_ui";
    /// <summary>The AG-UI activity type surfaces are carried in.</summary>
    public const string ActivityType = "a2ui-surface";
    /// <summary>Where the activity content holds the A2UI messages.</summary>
    public const string OperationsKey = "a2ui_operations";

    /// <summary>The A2UI v1.0 basic catalog id.</summary>
    public const string BasicCatalogId = "https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json";
    /// <summary>The Shiny Controls catalog id (basic + DataGrid, Gauge, ChipGroup, DateRangePicker, ...).</summary>
    public const string ShinyCatalogId = "https://shinylib.net/a2ui/shiny.controls/v1.json";

    readonly string description;

    /// <param name="catalogId">The catalog the model should build surfaces from.</param>
    /// <param name="catalogInstructions">The catalog's instructions and component summary. Pass the catalog document's
    /// <c>instructions</c> (and component names) so the model knows what it may place; without it the model
    /// must already know the catalog.</param>
    public AgUiRenderTool(string catalogId = ShinyCatalogId, string? catalogInstructions = null)
    {
        CatalogId = catalogId;
        description =
            "Shows interactive UI to the user (cards, forms, tables, charts, pickers) instead of describing it in text. " +
            "Pass A2UI v1.0 messages in 'operations': start with a createSurface " +
            $"(version \"1.0\", a new surfaceId, catalogId \"{catalogId}\", components as a flat list with one id \"root\", optional dataModel), " +
            "then updateComponents/updateDataModel to change it. Bind values with {\"@path\": \"/pointer\"}. " +
            "Buttons send an action back to you as the user's next message. Keep any text reply short: the UI speaks for itself." +
            (catalogInstructions is { Length: > 0 } ? "\n\nCatalog:\n" + catalogInstructions : "");
    }

    /// <summary>The catalog the model is told to use.</summary>
    public string CatalogId { get; }

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description => description;

    /// <inheritdoc />
    public override JsonElement JsonSchema { get; } = JsonElement.Parse("""
        {
          "type": "object",
          "properties": {
            "operations": {
              "type": "array",
              "description": "A2UI v1.0 messages, applied in order.",
              "items": { "type": "object" }
            }
          },
          "required": ["operations"]
        }
        """);

    /// <inheritdoc />
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => ValueTask.FromResult<object?>("The UI is on screen. Wait for the user to use it; their actions arrive as messages.");

    /// <summary>The A2UI messages from a call's arguments, or null if there are none.</summary>
    public static JsonArray? ReadOperations(string argumentsJson)
    {
        try
        {
            return JsonNode.Parse(argumentsJson)?["operations"] is JsonArray ops && ops.Count > 0
                ? (JsonArray)ops.DeepClone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
