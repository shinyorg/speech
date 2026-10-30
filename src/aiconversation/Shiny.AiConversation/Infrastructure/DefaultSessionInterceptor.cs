using Microsoft.Extensions.AI;

namespace Shiny.AiConversation.Infrastructure;

/// <summary>
/// A tool registered with <see cref="AiConversationOptions.AddTool"/>.
/// </summary>
/// <param name="Profiles">Limits the tool to sessions with one of these profiles; null or empty means every session.</param>
public record AiToolRegistration(AITool Tool, AiToolFlags Flags = AiToolFlags.None, IReadOnlyList<string>? Profiles = null);

/// <summary>
/// Always registered (ported from the v2 default context provider): adds registered tools (and any plain
/// <see cref="AITool"/> in DI) to every session, and the current local time to every turn.
/// </summary>
sealed class DefaultSessionInterceptor(
    TimeProvider timeProvider,
    IEnumerable<AiToolRegistration> registrations,
    IEnumerable<AITool> tools
) : ISessionInterceptor
{
    public Task OnSessionLoaded(SessionLoadedContext context)
    {
        var profile = context.Session.Settings.Profile;
        foreach (var reg in registrations)
        {
            if (reg.Profiles is { Count: > 0 } && (profile == null || !reg.Profiles.Contains(profile)))
                continue;

            context.AddTool(reg.Tool, reg.Flags);
        }

        foreach (var tool in tools)
            context.AddTool(tool);

        return Task.CompletedTask;
    }


    public Task OnBeforeTurn(TurnContext context)
    {
        var now = timeProvider.GetLocalNow();
        context.SystemPrompts.Add($"The current time is {now:hh:mm tt} on {now:MMMM dd, yyyy}.");
        return Task.CompletedTask;
    }
}
