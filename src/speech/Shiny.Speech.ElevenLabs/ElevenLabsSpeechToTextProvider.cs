using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Shiny.Audio;
using Shiny.Speech.Cloud;

namespace Shiny.Speech.ElevenLabs;

/// <summary>
/// Speech-to-text provider backed by the ElevenLabs Scribe transcription API.
/// Scribe itself is request/response (no native streaming), so this provider performs
/// client-side voice activity detection on the PCM mic stream: each speech-then-silence
/// segment becomes one POST to <c>/v1/speech-to-text</c> and one yielded
/// <see cref="SpeechRecognitionResult"/>. The session keeps running until the caller
/// cancels — typical "continuous recognition" behaviour layered on a one-shot API.
/// </summary>
public class ElevenLabsSpeechToTextProvider(
    ElevenLabsConfig config,
    ILogger<ElevenLabsSpeechToTextProvider> logger
) : ISpeechToTextProvider, IDisposable
{
    const int SampleRate = 16000;
    const short BitsPerSample = 16;
    const short Channels = 1;
    const int FrameDurationMs = 20;
    const int FrameSamples = SampleRate * FrameDurationMs / 1000; // 320
    const int FrameBytes = FrameSamples * (BitsPerSample / 8);    // 640

    // Rebuilds the HttpClient (which bakes in the xi-api-key header) if config.ApiKey changes at runtime.
    readonly RefreshableClient<HttpClient> http = new(() => CreateHttpClient(config));

    public event EventHandler<SpeechRecognitionError>? Error;

    static HttpClient CreateHttpClient(ElevenLabsConfig config)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.elevenlabs.io/")
        };
        client.DefaultRequestHeaders.Add("xi-api-key", config.ApiKey);
        return client;
    }

    public void Dispose() => this.http.Dispose();

    public async IAsyncEnumerable<SpeechRecognitionResult> RecognizeAsync(
        Stream audioStream,
        SpeechRecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new SpeechRecognitionOptions();

        var silenceTimeoutMs = (int)options.SilenceTimeout.TotalMilliseconds;
        if (silenceTimeoutMs <= 0)
            silenceTimeoutMs = 2000;

        var rmsThreshold = config.SilenceRmsThreshold;
        var minUtteranceMs = config.MinUtteranceDurationMs;
        var maxUtteranceMs = config.MaxUtteranceDurationMs;

        var utterance = new MemoryStream();
        var frame = new byte[FrameBytes];
        var silenceMs = 0;
        var speechMs = 0;
        var inSpeech = false;
        var endOfStream = false;

        while (!endOfStream)
        {
            int read;
            try
            {
                read = await ReadFullFrameAsync(audioStream, frame, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read < FrameBytes)
            {
                endOfStream = true;
                if (read == 0)
                    break;
            }

            var rms = ComputeRms(frame, read);

            if (rms > rmsThreshold)
            {
                utterance.Write(frame, 0, read);
                speechMs += FrameDurationMs;
                silenceMs = 0;
                inSpeech = true;

                if (speechMs >= maxUtteranceMs)
                {
                    var forced = await TranscribeAsync(utterance, options);
                    if (forced != null)
                        yield return forced;

                    utterance.SetLength(0);
                    inSpeech = false;
                    speechMs = 0;
                    silenceMs = 0;
                }
            }
            else if (inSpeech)
            {
                utterance.Write(frame, 0, read);
                silenceMs += FrameDurationMs;

                if (silenceMs >= silenceTimeoutMs)
                {
                    if (speechMs >= minUtteranceMs)
                    {
                        var result = await TranscribeAsync(utterance, options);
                        if (result != null)
                            yield return result;
                    }

                    utterance.SetLength(0);
                    inSpeech = false;
                    speechMs = 0;
                    silenceMs = 0;
                }
            }
            // else: pre-speech silence — discard the frame
        }

        // Flush any in-flight utterance on exit (cancellation or EOS).
        if (utterance.Length > 0 && speechMs >= minUtteranceMs)
        {
            var result = await TranscribeAsync(utterance, options);
            if (result != null)
                yield return result;
        }
    }

    async Task<SpeechRecognitionResult?> TranscribeAsync(
        MemoryStream pcm,
        SpeechRecognitionOptions options)
    {
        var wav = WavWriter.CreateFile(pcm.GetBuffer().AsSpan(0, (int)pcm.Length), SampleRate, Channels, BitsPerSample);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(config.SpeechToTextModel), "model_id");

        if (options.Culture != null)
            form.Add(new StringContent(options.Culture.TwoLetterISOLanguageName), "language_code");

        var audioContent = new ByteArrayContent(wav);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audioContent, "file", "audio.wav");

        logger.LogDebug("Sending {Bytes} bytes to ElevenLabs Scribe ({Model})", wav.Length, config.SpeechToTextModel);

        // Don't pipe the listening token into the POST — we want every utterance the
        // user already spoke to be delivered even if they tap Stop while we're
        // uploading. Bounded by a sane per-request timeout.
        using var postCts = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        HttpResponseMessage response;
        try
        {
            response = await this.http.Get(config.ApiKey).PostAsync("v1/speech-to-text", form, postCts.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ElevenLabs Scribe POST failed");
            Error?.Invoke(this, new SpeechRecognitionError(ex.Message, ex));
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(postCts.Token);
                logger.LogWarning("ElevenLabs Scribe error {Status}: {Body}", response.StatusCode, body);
                Error?.Invoke(this, new SpeechRecognitionError($"ElevenLabs Scribe error {response.StatusCode}: {body}"));
                return null;
            }

            var scribe = await response.Content.ReadFromJsonAsync<ScribeResponse>(postCts.Token);
            logger.LogDebug("ElevenLabs Scribe transcription completed");

            if (scribe?.Text is { Length: > 0 } text)
                return new SpeechRecognitionResult(text, true, (float?)scribe.LanguageProbability);
        }

        return null;
    }

    static async Task<int> ReadFullFrameAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (n == 0)
                return total;
            total += n;
        }
        return total;
    }

    static int ComputeRms(byte[] buffer, int length)
    {
        var samples = length / 2;
        if (samples == 0) return 0;

        long sumSquares = 0;
        for (int i = 0; i + 1 < length; i += 2)
        {
            var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumSquares += sample * sample;
        }
        return (int)Math.Sqrt(sumSquares / (double)samples);
    }

    sealed class ScribeResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("language_code")]
        public string? LanguageCode { get; set; }

        [JsonPropertyName("language_probability")]
        public double? LanguageProbability { get; set; }
    }
}
