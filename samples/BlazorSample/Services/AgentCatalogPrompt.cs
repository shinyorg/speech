using System.Text;
using System.Text.Json.Nodes;
using Shiny.Controls.AgentUI.A2UI;

namespace BlazorSample.Services;

/// <summary>
/// Builds the catalog text the <c>render_ui</c> tool hands the model: the A2UI basic catalog's rules and
/// examples, the Shiny catalog's instructions and one line per component with its properties. The full
/// catalog schema (<c>A2UICatalog.CreateShiny().Definition</c>) is ~58 KB, too much to resend with every
/// request, so this is the short version.
/// </summary>
/// <remarks>Only compiled when the sibling controls repo is present (see BlazorSample.csproj).</remarks>
static class AgentCatalogPrompt
{
    public static string Build()
    {
        var shiny = A2UICatalog.CreateShiny();
        var basic = A2UICatalog.CreateBasic();
        var sb = new StringBuilder();

        sb.AppendLine(Str(basic.Definition?["instructions"]));
        sb.AppendLine();
        sb.AppendLine(Str(shiny.Definition?["instructions"]));
        sb.AppendLine();
        sb.AppendLine("""
            Format notes: every component is {"id": "...", "component": "<Name>", ...properties}. A child property holds
            another component's id; a children property holds an array of ids, or a template
            {"componentId": "<id>", "path": "/array/pointer"} that repeats a component for each array item (paths inside it are
            relative to the item). Strings, numbers and booleans may be literals or bindings {"@path": "/pointer"}.
            A Button's action is {"event": {"name": "<name>", "context": {"key": {"@path": "/pointer"}}}}; the event comes
            back to you with its resolved context.
            """);
        sb.AppendLine("Components (* = required):");

        foreach (var c in shiny.Components.Values)
        {
            sb.Append("- ").Append(c.Name).Append(": ");
            sb.AppendJoin(", ", c.Properties.Where(p => !Common.Contains(p)).Select(p => Describe(c, p)));
            sb.AppendLine();
        }

        sb.Append("Functions (use with {\"@call\": \"name\", \"args\": {...}}): ");
        sb.AppendJoin(", ", shiny.Functions.Keys);
        sb.AppendLine();
        return sb.ToString();
    }


    // on every component (or almost); left out of the per-component lines to keep them short
    static readonly HashSet<string> Common = ["component", "id", "weight", "@catalogId", "@accessibility", "@metadata"];


    static string Describe(A2UIComponentDefinition c, string property)
    {
        var text = property + (c.RequiredProperties.Contains(property) ? "*" : "");
        var child = c.ChildProperties.FirstOrDefault(x => x.Name == property);
        if (child != null)
            return text + child.Kind switch
            {
                A2UIChildPropertyKind.Single => " (id)",
                A2UIChildPropertyKind.List => " (ids|template)",
                _ => $" ([{{{child.ItemProperty}: id}}])"
            };

        var schema = FindProperty(c.Schema, property);
        if (schema?["enum"] is JsonArray values)
            return text + " (" + String.Join("|", values.Select(v => v?.ToString())) + ")";

        // arrays of objects (DataGrid columns, ChipGroup options, ...): show the members of an item
        if (schema?["items"]?["properties"] is JsonObject members)
            return text + " ([{" + String.Join(", ", members.Select(m => m.Key)) + "}])";

        return text;
    }


    // a property's schema, looking through allOf
    static JsonObject? FindProperty(JsonNode? schema, string property)
    {
        if (schema is not JsonObject obj)
            return null;

        if (obj["properties"]?[property] is JsonObject found)
            return found;

        return obj["allOf"] is JsonArray all
            ? all.Select(x => FindProperty(x, property)).FirstOrDefault(x => x != null)
            : null;
    }


    static string Str(JsonNode? node) => node?.GetValue<string>() ?? "";
}


/// <summary>The session profile the Agent page starts its sessions with; the render_ui tool is scoped to it.</summary>
static class AgentProfile
{
    public const string Name = "agent";
}
