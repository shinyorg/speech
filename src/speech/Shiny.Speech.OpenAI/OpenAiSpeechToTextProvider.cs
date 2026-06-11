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
    public event EventHandler<SpeechRecognitionError>? Error;

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

        var result = await TryTranscribeAsync(client, ms, transcriptionOptions, cancellationToken);
        if (result == null)
            yield break;

        logger.LogDebug("OpenAI transcription completed");

        if (!string.IsNullOrEmpty(result.Text))
        {
            yield return new SpeechRecognitionResult(result.Text, true);
        }
    }

    async Task<global::OpenAI.Audio.AudioTranscription?> TryTranscribeAsync(
        AudioClient client,
        Stream audio,
        AudioTranscriptionOptions transcriptionOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.TranscribeAudioAsync(audio, "audio.wav", transcriptionOptions, cancellationToken);
            return result.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenAI transcription failed");
            Error?.Invoke(this, new SpeechRecognitionError(ex.Message, ex));
            return null;
        }
    }
}
