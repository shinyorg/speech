namespace Shiny.Speech.OpenAI;

/// <summary>
/// OpenAI speech credentials/options. Properties are mutable so they can be changed at runtime — the
/// provider reads them on each call, so updating <see cref="ApiKey"/> (or the models/voice) takes
/// effect on the next synthesis/recognition without re-registering anything.
/// </summary>
public record OpenAiSpeechConfig
{
    public required string ApiKey { get; set; }
    public string SpeechToTextModel { get; set; } = "gpt-4o-transcribe";
    public string TextToSpeechModel { get; set; } = "gpt-4o-mini-tts";
    public string DefaultVoice { get; set; } = "alloy";
}
