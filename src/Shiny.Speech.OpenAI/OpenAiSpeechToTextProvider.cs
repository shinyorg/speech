using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenAI.Audio;
using Shiny.Speech.Cloud;

namespace Shiny.Speech.OpenAI;

public class OpenAiSpeechToTextProvider(
    OpenAiSpeechConfig config,
    ILogger<OpenAiSpeechToTextProvider> logger
) : ISpeechToTextProvider
{
    public async IAsyncEnumerable<SpeechRecognitionResult> RecognizeAsync(
        Stream audioStream,
        SpeechRecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = new AudioClient(config.SpeechToTextModel, config.ApiKey);

        var transcriptionOptions = new AudioTranscriptionOptions();
        if (options?.Culture != null)
            transcriptionOptions.Language = options.Culture.Name;

        logger.LogDebug("Sending audio to OpenAI for transcription using model {Model}", config.SpeechToTextModel);

        // OpenAI transcription is not streaming - it returns a complete result
        // We need to buffer the PCM stream since OpenAI expects a file-like format
        using var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, cancellationToken);
        ms.Position = 0;

        var result = await client.TranscribeAudioAsync(
            ms,
            "audio.wav",
            transcriptionOptions,
            cancellationToken
        );

        logger.LogDebug("OpenAI transcription completed");

        if (!string.IsNullOrEmpty(result.Value.Text))
        {
            yield return new SpeechRecognitionResult(result.Value.Text, true);
        }
    }
}
