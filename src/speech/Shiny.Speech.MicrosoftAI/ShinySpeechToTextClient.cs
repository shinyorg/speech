using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Shiny.Speech.Cloud;

namespace Shiny.Speech.MicrosoftAI;

public class ShinySpeechToTextClient(
    ISpeechToTextProvider provider,
    IAudioSource audioSource
) : ISpeechToTextClient
{
    public async Task<SpeechToTextResponse> GetTextAsync(
        Stream audioStream,
        SpeechToTextOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var recognitionOptions = options.ToSpeechRecognitionOptions();
        var contents = new List<AIContent>();

        await foreach (var result in provider.RecognizeAsync(audioStream, recognitionOptions, cancellationToken))
        {
            if (result.IsFinal && !string.IsNullOrEmpty(result.Text))
            {
                contents.Add(new TextContent(result.Text));
            }
        }

        return new SpeechToTextResponse(contents)
        {
            ModelId = options?.ModelId
        };
    }

    public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(
        Stream audioStream,
        SpeechToTextOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var recognitionOptions = options.ToSpeechRecognitionOptions();

        yield return new SpeechToTextResponseUpdate
        {
            Kind = SpeechToTextResponseUpdateKind.SessionOpen
        };

        await foreach (var result in provider.RecognizeAsync(audioStream, recognitionOptions, cancellationToken))
        {
            if (string.IsNullOrEmpty(result.Text))
                continue;

            yield return new SpeechToTextResponseUpdate
            {
                Kind = result.IsFinal
                    ? SpeechToTextResponseUpdateKind.TextUpdated
                    : SpeechToTextResponseUpdateKind.TextUpdating,
                Contents = [new TextContent(result.Text)],
                ModelId = options?.ModelId
            };
        }

        yield return new SpeechToTextResponseUpdate
        {
            Kind = SpeechToTextResponseUpdateKind.SessionClose
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null)
        {
            if (serviceType == typeof(ISpeechToTextProvider))
                return provider;

            if (serviceType == typeof(IAudioSource))
                return audioSource;
        }

        return null;
    }

    public void Dispose()
    {
    }
}
