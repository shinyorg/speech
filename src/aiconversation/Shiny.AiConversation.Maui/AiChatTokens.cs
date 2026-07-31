using System.Globalization;

namespace Shiny.AiConversation.Maui;

/// <summary>Formats the per-message token usage footer appended when AiChatView.ShowTokenUsage is set.</summary>
static class AiChatTokens
{
    public static string AppendTokenFooter(string body, long? input, long? output, long? total)
    {
        var footer = FormatTokenFooter(input, output, total);
        return footer is null ? body : body + "\n\n— " + footer;
    }

    public static string? FormatTokenFooter(long? input, long? output, long? total)
    {
        if (total is null && input is null && output is null)
            return null;

        var ci = CultureInfo.InvariantCulture;
        var totalStr = (total ?? ((input ?? 0) + (output ?? 0))).ToString("N0", ci);

        if (input.HasValue && output.HasValue)
            return $"{totalStr} tokens ({input.Value.ToString("N0", ci)} in · {output.Value.ToString("N0", ci)} out)";

        return $"{totalStr} tokens";
    }
}
