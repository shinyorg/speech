using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ShinySpeech = Shiny.Speech;

namespace Shiny.AiConversation.Infrastructure;

public class VoiceSelectionContextProvider(
    ShinySpeech.ITextToSpeechService textToSpeech,
    IServiceProvider services
) : IContextProvider
{
    // Resolved lazily to break the circular dependency:
    // AiConversationService → IEnumerable<IContextProvider> → VoiceSelectionContextProvider → IAiConversationService
    IAiConversationService AiService => services.GetRequiredService<IAiConversationService>();

    public Task Apply(AiContext context)
    {
        context.Tools.Add(AIFunctionFactory.Create(
            this.GetAvailableVoices,
            "get_available_voices",
            "Returns a list of available text-to-speech voices the AI can use to speak responses."
        ));
        context.Tools.Add(AIFunctionFactory.Create(
            this.PlayVoiceSample,
            "play_voice_sample",
            "Plays a spoken sample using the specified voice so the user can hear how it sounds before selecting it. Call this for each voice the user wants to preview."
        ));
        context.Tools.Add(AIFunctionFactory.Create(
            this.ChangeVoice,
            "change_voice",
            "Changes the AI's text-to-speech voice to the specified voice ID. Use get_available_voices first to find valid IDs."
        ));

        return Task.CompletedTask;
    }

    [Description("Returns a list of available text-to-speech voices")]
    async Task<string> GetAvailableVoices(
        [Description("Optional BCP-47 culture code to filter voices (e.g. 'en-US')")] string? culture = null,
        CancellationToken cancellationToken = default
    )
    {
        var cultureInfo = culture != null ? new CultureInfo(culture) : null;
        var voices = await textToSpeech.GetVoicesAsync(cultureInfo, cancellationToken);

        if (voices.Count == 0)
            return "No voices available.";

        var lines = voices.Select(v => $"ID: {v.Id} | Name: {v.Name} | Culture: {v.Culture.Name}");
        return string.Join("\n", lines);
    }

    [Description("Plays a spoken audio sample using a specific voice so the user can hear how it sounds")]
    async Task<string> PlayVoiceSample(
        [Description("The voice ID to sample (from get_available_voices)")] string voiceId,
        [Description("Optional custom text to speak. Defaults to a standard greeting.")] string? sampleText = null,
        CancellationToken cancellationToken = default
    )
    {
        var voices = await textToSpeech.GetVoicesAsync(cancellationToken: cancellationToken);
        var voice = voices.FirstOrDefault(v => v.Id == voiceId);

        if (voice == null)
            return $"Voice '{voiceId}' not found. Use get_available_voices to see valid voice IDs.";

        var text = sampleText ?? "Hello! I'm your AI assistant. How does this voice sound to you?";
        await textToSpeech.SpeakAsync(text, new ShinySpeech.TextToSpeechOptions { Voice = voice }, cancellationToken);
        return $"Played sample for voice: {voice.Name} ({voice.Culture.Name})";
    }

    [Description("Changes the AI's speaking voice to the specified voice")]
    async Task<string> ChangeVoice(
        [Description("The voice ID to switch to (from get_available_voices)")] string voiceId,
        CancellationToken cancellationToken = default
    )
    {
        var voices = await textToSpeech.GetVoicesAsync(cancellationToken: cancellationToken);
        var voice = voices.FirstOrDefault(v => v.Id == voiceId);

        if (voice == null)
            return $"Voice '{voiceId}' not found. Use get_available_voices to see valid voice IDs.";

        this.AiService.TextToSpeechOptions = new ShinySpeech.TextToSpeechOptions { Voice = voice };
        return $"Voice changed to {voice.Name}.";
    }
}
