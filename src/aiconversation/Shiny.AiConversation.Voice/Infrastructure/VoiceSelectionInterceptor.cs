using System.Globalization;
using Shiny.AiConversation.Infrastructure;
using Shiny.Speech;

namespace Shiny.AiConversation.Voice.Infrastructure;

/// <summary>
/// Lets the AI list voices, play a sample and change its own voice. Acts on the voice conversation
/// directly (no service-locator lookup), so there is no DI cycle.
/// </summary>
sealed class VoiceSelectionInterceptor(ITextToSpeechService textToSpeech, IVoiceConversation voice) : ISessionInterceptor
{
    public const string ListToolName = "get_available_voices";
    public const string SampleToolName = "play_voice_sample";
    public const string ChangeToolName = "change_voice";

    public Task OnSessionLoaded(SessionLoadedContext context)
    {
        context.AddTool(new SchemaFunction(
            ListToolName,
            "Lists the text-to-speech voices you can speak with.",
            """{ "type": "object", "properties": { "culture": { "type": "string", "description": "Optional BCP-47 culture to filter by, e.g. en-US." } } }""",
            (args, ct) => this.List(args.GetString("culture"), ct)
        ), AiToolFlags.Silent);

        context.AddTool(new SchemaFunction(
            SampleToolName,
            "Plays a short spoken sample of a voice so the user can hear it before choosing.",
            """{ "type": "object", "properties": { "voiceId": { "type": "string" }, "sampleText": { "type": "string" } }, "required": ["voiceId"] }""",
            (args, ct) => this.Sample(args.GetString("voiceId"), args.GetString("sampleText"), ct)
        ));

        context.AddTool(new SchemaFunction(
            ChangeToolName,
            "Changes the voice you speak with. Use get_available_voices first to find a valid id.",
            """{ "type": "object", "properties": { "voiceId": { "type": "string" } }, "required": ["voiceId"] }""",
            (args, ct) => this.Change(args.GetString("voiceId"), ct)
        ), AiToolFlags.Silent);

        return Task.CompletedTask;
    }


    async Task<string> List(string? culture, CancellationToken ct)
    {
        CultureInfo? info = null;
        if (!String.IsNullOrWhiteSpace(culture))
        {
            try
            {
                info = new CultureInfo(culture);
            }
            catch (CultureNotFoundException)
            {
                return $"Error: unknown culture '{culture}'";
            }
        }

        var voices = await textToSpeech.GetVoicesAsync(info, ct).ConfigureAwait(false);
        if (voices.Count == 0)
            return "No voices available.";

        return String.Join("\n", voices.Select(v => $"ID: {v.Id} | Name: {v.Name} | Culture: {v.Culture.Name}"));
    }


    async Task<string> Sample(string? voiceId, string? sampleText, CancellationToken ct)
    {
        var found = await this.Find(voiceId, ct).ConfigureAwait(false);
        if (found == null)
            return $"Error: voice '{voiceId}' not found; use {ListToolName} to see valid ids";

        var options = (voice.TextToSpeech ?? new TextToSpeechOptions()) with { Voice = found };
        await textToSpeech.SpeakAsync(sampleText ?? "Hello! How does this voice sound to you?", options, ct).ConfigureAwait(false);
        return $"Played a sample of {found.Name} ({found.Culture.Name})";
    }


    async Task<string> Change(string? voiceId, CancellationToken ct)
    {
        var found = await this.Find(voiceId, ct).ConfigureAwait(false);
        if (found == null)
            return $"Error: voice '{voiceId}' not found; use {ListToolName} to see valid ids";

        voice.TextToSpeech = (voice.TextToSpeech ?? new TextToSpeechOptions()) with { Voice = found };
        return $"Voice changed to {found.Name}.";
    }


    async Task<VoiceInfo?> Find(string? voiceId, CancellationToken ct)
    {
        if (String.IsNullOrWhiteSpace(voiceId))
            return null;

        var voices = await textToSpeech.GetVoicesAsync(cancellationToken: ct).ConfigureAwait(false);
        return voices.FirstOrDefault(v => v.Id == voiceId);
    }
}
