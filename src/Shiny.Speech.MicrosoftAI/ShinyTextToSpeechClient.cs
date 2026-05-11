using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Shiny.Speech.Cloud;

using MeaiTextToSpeechOptions = Microsoft.Extensions.AI.TextToSpeechOptions;

namespace Shiny.Speech.MicrosoftAI;

public class ShinyTextToSpeechClient(
    ITextToSpeechProvider provider
) : ITextToSpeechClient
{
    public async Task<TextToSpeechResponse> GetAudioAsync(
        string text,
        MeaiTextToSpeechOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var ttsOptions = options.ToShinyTextToSpeechOptions();
        var audioStream = await provider.SynthesizeAsync(text, ttsOptions, cancellationToken);

        using var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, cancellationToken);

        var audioContent = new DataContent(ms.ToArray(), options?.AudioFormat ?? "audio/mpeg");

        return new TextToSpeechResponse([audioContent])
        {
            ModelId = options?.ModelId
        };
    }

    public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(
        string text,
        MeaiTextToSpeechOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        yield return new TextToSpeechResponseUpdate
        {
            Kind = TextToSpeechResponseUpdateKind.SessionOpen
        };

        var ttsOptions = options.ToShinyTextToSpeechOptions();
        var audioStream = await provider.SynthesizeAsync(text, ttsOptions, cancellationToken);

        using var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, cancellationToken);

        var audioContent = new DataContent(ms.ToArray(), options?.AudioFormat ?? "audio/mpeg");

        yield return new TextToSpeechResponseUpdate
        {
            Kind = TextToSpeechResponseUpdateKind.AudioUpdated,
            Contents = [audioContent],
            ModelId = options?.ModelId
        };

        yield return new TextToSpeechResponseUpdate
        {
            Kind = TextToSpeechResponseUpdateKind.SessionClose
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(ITextToSpeechProvider))
            return provider;

        return null;
    }

    public void Dispose()
    {
    }
}
