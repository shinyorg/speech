using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Shiny.Speech.Cloud;
using Whisper.net;

namespace Shiny.Speech.Linux;

/// <summary>
/// Speech-to-text provider that runs Whisper on-device via whisper.cpp — no cloud account, no
/// network, no per-minute billing. Built for Linux boxes and SBCs (Raspberry Pi) where there is no
/// OS speech engine to wrap.
/// </summary>
/// <remarks>
/// <para>
/// Whisper is a batch model with a 30-second window, not a streaming recognizer, so this provider
/// does client-side voice activity detection on the PCM mic stream: each speech-then-silence segment
/// becomes one inference pass and one final <see cref="SpeechRecognitionResult"/>. There are no
/// partial results — every result has <c>IsFinal = true</c>. The session runs until the caller
/// cancels, giving continuous recognition on top of a one-shot model, exactly like the ElevenLabs
/// Scribe provider does over HTTP.
/// </para>
/// <para>
/// Inference is CPU-bound and, on a Pi, is not free: expect roughly realtime with <c>Base</c> and
/// slower than realtime above <c>Small</c>. Suits push-to-talk and wake-word-then-command flows far
/// better than continuous dictation.
/// </para>
/// </remarks>
public class WhisperSpeechToTextProvider : ISpeechToTextProvider, IDisposable
{
    const int SampleRate = 16000;
    const short BitsPerSample = 16;
    const int FrameDurationMs = 20;
    const int FrameSamples = SampleRate * FrameDurationMs / 1000; // 320
    const int FrameBytes = FrameSamples * (BitsPerSample / 8);    // 640

    // whisper.cpp needs at least a second of audio to build a mel spectrogram; shorter utterances
    // are zero-padded up to this rather than rejected by the native layer.
    const int MinSamples = SampleRate;

    // [BLANK_AUDIO], (wind blowing), ♪ music ♪ — Whisper's non-speech annotations.
    static readonly Regex NonSpeechAnnotation = new(
        @"\[[^\]]*\]|\([^)]*\)|♪[^♪]*♪|♪",
        RegexOptions.Compiled
    );

    readonly WhisperConfig config;
    readonly ILogger<WhisperSpeechToTextProvider> logger;
    readonly SemaphoreSlim modelLock = new(1, 1);
    WhisperFactory? factory;

    public WhisperSpeechToTextProvider(
        WhisperConfig config,
        ILogger<WhisperSpeechToTextProvider> logger)
    {
        this.config = config;
        this.logger = logger;
    }

    public event EventHandler<SpeechRecognitionError>? Error;

    /// <summary>
    /// Download (if necessary) and load the model up front. Optional — the provider does this lazily
    /// on the first recognition — but the first load is seconds of CPU plus a potentially large
    /// download, so a daemon or kiosk app should call this at startup instead of making the user's
    /// first utterance pay for it.
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
        => await this.GetFactoryAsync(cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<SpeechRecognitionResult> RecognizeAsync(
        Stream audioStream,
        SpeechRecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Shiny.Speech.Linux.Whisper runs on Linux only.");

        options ??= new SpeechRecognitionOptions();

        var silenceTimeoutMs = (int)options.SilenceTimeout.TotalMilliseconds;
        if (silenceTimeoutMs <= 0)
            silenceTimeoutMs = 2000;

        var whisperFactory = await this.GetFactoryAsync(cancellationToken).ConfigureAwait(false);

        // One processor per session: it carries the decoder options (language, prompt) which are
        // fixed for the session, and it is not safe to use concurrently — the loop below is strictly
        // sequential, so a single instance is both correct and avoids re-allocating decoder state
        // for every utterance.
        using var processor = this.BuildProcessor(whisperFactory, options);

        var rmsThreshold = this.config.SilenceRmsThreshold;
        var minUtteranceMs = this.config.MinUtteranceDurationMs;
        var maxUtteranceMs = this.config.MaxUtteranceDurationMs;

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
                read = await ReadFullFrameAsync(audioStream, frame, cancellationToken).ConfigureAwait(false);
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
                    var forced = await this.TranscribeAsync(processor, utterance).ConfigureAwait(false);
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
                        var result = await this.TranscribeAsync(processor, utterance).ConfigureAwait(false);
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

        // Flush any in-flight utterance on exit (cancellation or end of stream) so tapping Stop
        // mid-sentence still delivers what was already spoken.
        if (utterance.Length > 0 && speechMs >= minUtteranceMs)
        {
            var result = await this.TranscribeAsync(processor, utterance).ConfigureAwait(false);
            if (result != null)
                yield return result;
        }
    }

    async Task<WhisperFactory> GetFactoryAsync(CancellationToken cancellationToken)
    {
        if (this.factory != null)
            return this.factory;

        await this.modelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.factory != null)
                return this.factory;

            var path = await WhisperModelResolver
                .ResolveAsync(this.config, this.logger, cancellationToken)
                .ConfigureAwait(false);

            this.logger.LogInformation("Loading whisper model {Path}", path);
            this.factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions
            {
                UseGpu = this.config.UseGpu
            });
            this.logger.LogInformation("Whisper model loaded");

            return this.factory;
        }
        finally
        {
            this.modelLock.Release();
        }
    }

    WhisperProcessor BuildProcessor(WhisperFactory whisperFactory, SpeechRecognitionOptions options)
    {
        // A session Culture wins over the configured default; "auto" lets Whisper detect per utterance.
        var language = options.Culture?.TwoLetterISOLanguageName ?? this.config.Language;

        var builder = whisperFactory
            .CreateBuilder()
            .WithLanguage(language)
            .WithThreads(this.config.Threads ?? DefaultThreads)
            .WithNoSpeechThreshold(this.config.NoSpeechThreshold)
            .WithProbabilities();

        if (!this.config.CarryContextBetweenUtterances)
            builder = builder.WithNoContext();

        if (this.config.Translate)
            builder = builder.WithTranslate();

        if (!String.IsNullOrWhiteSpace(this.config.InitialPrompt))
            builder = builder.WithPrompt(this.config.InitialPrompt);

        this.logger.LogDebug(
            "Whisper processor built (model: {Model}, language: {Language}, threads: {Threads})",
            this.config.ModelType,
            language,
            this.config.Threads ?? DefaultThreads
        );
        return builder.Build();
    }

    // Leave a core for audio capture — it matters on a 4-core Pi and costs nothing on a big box,
    // where whisper.cpp stops scaling past a handful of threads anyway.
    static int DefaultThreads => Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 4));

    async Task<SpeechRecognitionResult?> TranscribeAsync(WhisperProcessor processor, MemoryStream pcm)
    {
        var samples = ToFloatSamples(pcm.GetBuffer().AsSpan(0, (int)pcm.Length));

        // Don't pipe the listening token in — a user tapping Stop should still get the utterance
        // they already spoke. Bounded instead by a (generous) per-inference timeout, since a large
        // model on a slow ARM core is slow rather than stuck.
        using var cts = new CancellationTokenSource(this.config.TranscriptionTimeout);

        this.logger.LogDebug("Transcribing {Ms}ms of audio locally", samples.Length * 1000 / SampleRate);

        var text = new StringBuilder();
        var probabilitySum = 0f;
        var segments = 0;

        try
        {
            await foreach (var segment in processor.ProcessAsync(samples.AsMemory(), cts.Token).ConfigureAwait(false))
            {
                var segmentText = this.config.FilterNonSpeechAnnotations
                    ? NonSpeechAnnotation.Replace(segment.Text, " ")
                    : segment.Text;

                segmentText = segmentText.Trim();
                if (segmentText.Length == 0)
                    continue;

                if (text.Length > 0)
                    text.Append(' ');
                text.Append(segmentText);

                probabilitySum += segment.Probability;
                segments++;
            }
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Whisper transcription failed");
            this.Error?.Invoke(this, new SpeechRecognitionError(ex.Message, ex));
            return null;
        }

        if (text.Length == 0)
        {
            // Silence, music, or an utterance that filtered down to nothing but annotations.
            this.logger.LogDebug("Whisper returned no speech for this utterance");
            return null;
        }

        this.logger.LogDebug("Whisper transcription completed ({Segments} segments)", segments);
        return new SpeechRecognitionResult(text.ToString(), true, probabilitySum / segments);
    }

    /// <summary>
    /// Converts 16-bit little-endian PCM to the normalized float samples whisper.cpp expects,
    /// zero-padding short utterances up to <see cref="MinSamples"/>.
    /// </summary>
    static float[] ToFloatSamples(ReadOnlySpan<byte> pcm)
    {
        var sampleCount = pcm.Length / 2;
        var samples = new float[Math.Max(sampleCount, MinSamples)];

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(pcm[i * 2] | (pcm[(i * 2) + 1] << 8));
            samples[i] = sample / 32768f;
        }

        // Any tail beyond sampleCount stays 0f — i.e. silence.
        return samples;
    }

    static async Task<int> ReadFullFrameAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (n == 0)
                return total;
            total += n;
        }
        return total;
    }

    static int ComputeRms(byte[] buffer, int length)
    {
        var samples = length / 2;
        if (samples == 0)
            return 0;

        long sumSquares = 0;
        for (var i = 0; i + 1 < length; i += 2)
        {
            var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumSquares += sample * sample;
        }
        return (int)Math.Sqrt(sumSquares / (double)samples);
    }

    public void Dispose()
    {
        this.factory?.Dispose();
        this.factory = null;
        this.modelLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
