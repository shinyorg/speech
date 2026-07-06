namespace Shiny.Speech.ElevenLabs;

public record ElevenLabsConfig
{
    public required string ApiKey { get; set; }

    /// <summary>
    /// Default voice ID to use when no voice is specified in options.
    /// Default: "21m00Tcm4TlvDq8ikWAM" (Rachel)
    /// </summary>
    public string DefaultVoiceId { get; set; } = "21m00Tcm4TlvDq8ikWAM";

    /// <summary>
    /// The TTS model to use. Default: "eleven_multilingual_v2"
    /// </summary>
    public string TextToSpeechModel { get; set; } = "eleven_multilingual_v2";

    /// <summary>
    /// The Scribe STT model to use. Default: "scribe_v1"
    /// </summary>
    public string SpeechToTextModel { get; set; } = "scribe_v1";

    /// <summary>
    /// RMS energy threshold (0–32767) for 16-bit PCM frames. Frames below this are
    /// treated as silence; frames above as speech. Scribe is request/response, so the
    /// provider chunks the mic stream using VAD: each speech→silence segment becomes
    /// one POST to /v1/speech-to-text and one yielded <see cref="SpeechRecognitionResult"/>.
    /// Tune up in noisy rooms, down for quiet speakers. Default: 500.
    /// </summary>
    public int SilenceRmsThreshold { get; set; } = 500;

    /// <summary>
    /// Minimum cumulative speech duration before an utterance is sent for transcription.
    /// Filters out micro-blips (mouse clicks, breaths, door slams) that would otherwise
    /// fire a wasted POST. Default: 300 ms.
    /// </summary>
    public int MinUtteranceDurationMs { get; set; } = 300;

    /// <summary>
    /// Hard cap on a single utterance buffer. If a speaker doesn't pause for this long,
    /// the buffer is force-flushed to Scribe as one chunk to keep payload size and
    /// memory bounded. Default: 30 s.
    /// </summary>
    public int MaxUtteranceDurationMs { get; set; } = 30_000;
}
