namespace Shiny.Speech.OpenAI;

public record OpenAiSpeechConfig
{
    public required string ApiKey { get; init; }
    public string SpeechToTextModel { get; init; } = "gpt-4o-transcribe";
    public string TextToSpeechModel { get; init; } = "gpt-4o-mini-tts";
    public string DefaultVoice { get; init; } = "alloy";
}
