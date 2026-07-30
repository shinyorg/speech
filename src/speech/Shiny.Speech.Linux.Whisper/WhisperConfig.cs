using Whisper.net.Ggml;

namespace Shiny.Speech.Linux;

/// <summary>
/// Options for the on-device Whisper speech-to-text provider. Properties are mutable, but the model
/// (<see cref="ModelType"/> / <see cref="Quantization"/> / <see cref="ModelPath"/>) is read once when
/// the provider first loads it — change those before the first <c>Start()</c>, not after.
/// </summary>
public record WhisperConfig
{
    /// <summary>
    /// Which ggml Whisper model to run. Bigger is more accurate and much slower.
    /// <list type="bullet">
    /// <item><description><c>Tiny</c> / <c>TinyEn</c> — ~75 MB. Comfortably faster than realtime on a Pi 4.</description></item>
    /// <item><description><c>Base</c> / <c>BaseEn</c> — ~142 MB. The sweet spot on a Pi 4/5 for short commands. <b>Default.</b></description></item>
    /// <item><description><c>Small</c> / <c>SmallEn</c> — ~466 MB. Around or below realtime on a Pi 5; fine on an x64 box.</description></item>
    /// <item><description><c>Medium</c> and the <c>Large</c> variants — desktop/server class only.</description></item>
    /// </list>
    /// The <c>*En</c> variants are English-only and noticeably better than the multilingual model of
    /// the same size when you only need English.
    /// </summary>
    public GgmlType ModelType { get; set; } = GgmlType.Base;

    /// <summary>
    /// Quantization of the downloaded model. Quantized models are substantially smaller and faster
    /// with a modest accuracy cost — worth it on constrained ARM boards. Ignored when
    /// <see cref="ModelPath"/> points at a model you supplied yourself.
    /// Default: <see cref="QuantizationType.NoQuantization"/>.
    /// </summary>
    public QuantizationType Quantization { get; set; } = QuantizationType.NoQuantization;

    /// <summary>
    /// Explicit path to a ggml <c>.bin</c> model file. Leave <c>null</c> to use a file under
    /// <see cref="ModelDirectory"/> named for <see cref="ModelType"/> / <see cref="Quantization"/>.
    /// If the path doesn't exist and <see cref="AutoDownloadModel"/> is true, the model is downloaded
    /// to it.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// Directory used to cache downloaded models when <see cref="ModelPath"/> is not set.
    /// Default: <c>$XDG_DATA_HOME/shiny.speech/whisper</c> (i.e. <c>~/.local/share/shiny.speech/whisper</c>).
    /// </summary>
    public string? ModelDirectory { get; set; }

    /// <summary>
    /// Download the model from Hugging Face on first use when it isn't already on disk. Set false to
    /// require a pre-provisioned model — a missing file then throws instead of pulling ~75–466 MB
    /// over the network. Default: true.
    /// </summary>
    /// <remarks>
    /// Set the <c>HF_TOKEN</c> environment variable if you hit Hugging Face rate limiting. Prefer
    /// calling <c>PrepareAsync()</c> at startup over paying the download + model load on the first
    /// utterance.
    /// </remarks>
    public bool AutoDownloadModel { get; set; } = true;

    /// <summary>
    /// Inference threads. Default: one fewer than <see cref="Environment.ProcessorCount"/>, capped at
    /// 4 — leaving a core for audio capture matters on a 4-core Pi.
    /// </summary>
    public int? Threads { get; set; }

    /// <summary>
    /// Ask Whisper.net to use a GPU runtime. Only meaningful when a GPU runtime package
    /// (<c>Whisper.net.Runtime.Cuda</c>, <c>Whisper.net.Runtime.Vulkan</c>) is also referenced —
    /// the CPU runtime shipped with this package ignores it. Default: false.
    /// </summary>
    public bool UseGpu { get; set; }

    /// <summary>
    /// BCP-47 language code to transcribe (e.g. <c>"en"</c>, <c>"de"</c>), or <c>"auto"</c> to let
    /// Whisper detect it per utterance. Overridden per session by
    /// <see cref="SpeechRecognitionOptions.Culture"/>. Default: <c>"auto"</c>.
    /// </summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Translate to English instead of transcribing in the spoken language. Default: false.
    /// </summary>
    public bool Translate { get; set; }

    /// <summary>
    /// Optional prompt biasing the decoder toward specific vocabulary — product names, jargon,
    /// people. e.g. <c>"Shiny, MAUI, Blazor, Allan Ritchie"</c>.
    /// </summary>
    public string? InitialPrompt { get; set; }

    /// <summary>
    /// Feed the previous utterance's tokens into the next as context. Improves continuity in
    /// dictation, but lets a bad transcription poison everything after it — Whisper's well-known
    /// repetition loops. Default: false (each utterance decoded independently).
    /// </summary>
    public bool CarryContextBetweenUtterances { get; set; }

    /// <summary>
    /// Drop segments Whisper marks as non-speech above this probability. Raise toward 1.0 to keep
    /// more marginal audio, lower to suppress more. Default: 0.6.
    /// </summary>
    public float NoSpeechThreshold { get; set; } = 0.6f;

    /// <summary>
    /// Strip bracketed non-speech annotations (<c>[BLANK_AUDIO]</c>, <c>(wind blowing)</c>, <c>♪</c>)
    /// that Whisper emits for music and silence, and discard the result if nothing else remains.
    /// Without this, an open mic in a quiet room periodically "recognizes" <c>[BLANK_AUDIO]</c>.
    /// Default: true.
    /// </summary>
    public bool FilterNonSpeechAnnotations { get; set; } = true;

    /// <summary>
    /// RMS energy threshold (0–32767) for 16-bit PCM frames. Frames below this are treated as
    /// silence, frames above as speech. Whisper is a batch model, so the provider chunks the mic
    /// stream using this VAD: each speech→silence segment becomes one inference and one yielded
    /// <see cref="SpeechRecognitionResult"/>. Tune up in noisy rooms, down for quiet speakers.
    /// Default: 500.
    /// </summary>
    public int SilenceRmsThreshold { get; set; } = 500;

    /// <summary>
    /// Minimum cumulative speech duration before an utterance is transcribed. Filters out
    /// micro-blips (clicks, breaths, door slams) that would otherwise burn a full inference pass —
    /// far more expensive here than the cloud providers' wasted HTTP request. Default: 300 ms.
    /// </summary>
    public int MinUtteranceDurationMs { get; set; } = 300;

    /// <summary>
    /// Hard cap on a single utterance buffer. If the speaker doesn't pause for this long, the buffer
    /// is force-flushed as one chunk to keep memory and inference latency bounded. Default: 30 s —
    /// Whisper's native window, beyond which whisper.cpp chunks internally anyway.
    /// </summary>
    public int MaxUtteranceDurationMs { get; set; } = 30_000;

    /// <summary>
    /// Ceiling on a single inference pass, after which the utterance is abandoned and surfaced via
    /// the provider's <c>Error</c> event. Generous by default because a large model on a slow ARM
    /// core is genuinely slow, not hung. Default: 5 minutes.
    /// </summary>
    public TimeSpan TranscriptionTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
