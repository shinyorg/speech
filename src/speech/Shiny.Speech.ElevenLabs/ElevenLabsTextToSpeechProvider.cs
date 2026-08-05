using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Shiny.Speech.Cloud;

namespace Shiny.Speech.ElevenLabs;

public class ElevenLabsTextToSpeechProvider(
    ElevenLabsConfig config,
    ILogger<ElevenLabsTextToSpeechProvider> logger
) : ITextToSpeechProvider, IDisposable
{
    // Rebuilds the HttpClient (which bakes in the xi-api-key header) if config.ApiKey changes at runtime.
    readonly RefreshableClient<HttpClient> http = new(() => CreateHttpClient(config));

    /// <summary>
    /// Audio tags are an Eleven v3 feature. Older models (multilingual v2, turbo, flash) read them
    /// aloud as text, so capabilities are derived from the configured model rather than hardcoded —
    /// switching <see cref="ElevenLabsConfig.TextToSpeechModel"/> automatically switches annotation
    /// handling between "perform" and "strip".
    /// </summary>
    public SpeechToneCapabilities ToneCapabilities
        => config.TextToSpeechModel.StartsWith("eleven_v3", StringComparison.OrdinalIgnoreCase)
            ? SpeechToneCapabilities.InlineAnnotations
            : SpeechToneCapabilities.None;

    static HttpClient CreateHttpClient(ElevenLabsConfig config)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.elevenlabs.io/")
        };
        client.DefaultRequestHeaders.Add("xi-api-key", config.ApiKey);
        return client;
    }

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CultureInfo? culture = null, CancellationToken cancellationToken = default)
    {
        var httpClient = this.http.Get(config.ApiKey);
        var response = await httpClient.GetAsync("v1/voices", cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<VoicesResponse>(cancellationToken);
        if (result?.Voices == null)
            return [];

        var voices = new List<VoiceInfo>();
        foreach (var v in result.Voices)
        {
            if (v.VoiceId == null || v.Name == null)
                continue;

            var voiceCulture = CultureInfo.InvariantCulture;
            if (v.Labels?.TryGetValue("language", out var lang) == true && lang != null)
            {
                try { voiceCulture = new CultureInfo(lang); }
                catch { /* use invariant */ }
            }

            if (culture == null || voiceCulture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName)
                voices.Add(new VoiceInfo(v.VoiceId, v.Name, voiceCulture));
        }

        logger.LogDebug("ElevenLabs returned {Count} voices", voices.Count);
        return voices;
    }

    public async Task<Stream> SynthesizeAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new TextToSpeechOptions();
        var httpClient = this.http.Get(config.ApiKey);
        var voiceId = options.Voice?.Id ?? config.DefaultVoiceId;

        // On v3 this leaves audio tags in place (and prepends one for an explicit Tone); on every
        // older model it strips them so they aren't spoken aloud.
        var resolved = SpeechAnnotations.Resolve(text, options, this.ToneCapabilities);

        var requestBody = new TtsRequest
        {
            Text = resolved.Text,
            ModelId = config.TextToSpeechModel,
            VoiceSettings = new VoiceSettings
            {
                Stability = 0.5f,
                SimilarityBoost = 0.75f
            }
        };

        var response = await httpClient.PostAsJsonAsync($"v1/text-to-speech/{voiceId}", requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        var audioStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, cancellationToken);
        ms.Position = 0;

        logger.LogDebug("ElevenLabs TTS synthesized {Bytes} bytes", ms.Length);
        return ms;
    }

    public void Dispose() => this.http.Dispose();

    sealed record VoicesResponse
    {
        [JsonPropertyName("voices")]
        public List<VoiceEntry>? Voices { get; init; }
    }

    sealed record VoiceEntry
    {
        [JsonPropertyName("voice_id")]
        public string? VoiceId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("labels")]
        public Dictionary<string, string?>? Labels { get; init; }
    }

    sealed record TtsRequest
    {
        [JsonPropertyName("text")]
        public required string Text { get; init; }

        [JsonPropertyName("model_id")]
        public required string ModelId { get; init; }

        [JsonPropertyName("voice_settings")]
        public required VoiceSettings VoiceSettings { get; init; }
    }

    sealed record VoiceSettings
    {
        [JsonPropertyName("stability")]
        public float Stability { get; init; }

        [JsonPropertyName("similarity_boost")]
        public float SimilarityBoost { get; init; }
    }
}
