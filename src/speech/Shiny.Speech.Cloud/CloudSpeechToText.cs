using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Shiny.Audio;
using Shiny.Speech;

namespace Shiny.Speech.Cloud;

/// <summary>
/// ISpeechToTextService implementation that captures audio from the platform microphone
/// and delegates recognition to a pluggable ISpeechToTextProvider (Azure, Google, etc.).
/// </summary>
public class CloudSpeechToText : ISpeechToTextService
{
    readonly ISpeechToTextProvider provider;
    readonly IAudioSource audioSource;
    readonly ILogger<CloudSpeechToText> logger;

    CancellationTokenSource? cts;
    Regex? keywordPattern;
    Task? recognitionTask;

    public CloudSpeechToText(
        ISpeechToTextProvider provider,
        IAudioSource audioSource,
        ILogger<CloudSpeechToText> logger)
    {
        this.provider = provider;
        this.audioSource = audioSource;
        this.logger = logger;

        // Forward non-fatal provider errors (e.g. transient network failures during
        // continuous recognition) to the service-level Error event.
        provider.Error += (_, err) => Error?.Invoke(this, err);
    }

    // Dedup state — suppress same-text keyword re-fires within a short window.
    string? lastKeywordFinalText;
    DateTime lastKeywordFinalTime;
    static readonly TimeSpan KeywordDedupWindow = TimeSpan.FromSeconds(3);

    public bool IsSupported => true;
    public bool IsListening { get; private set; }

    public event EventHandler<SpeechRecognitionResult>? ResultReceived;
    public event EventHandler<string>? KeywordHeard;
    public event EventHandler<SpeechRecognitionError>? Error;

    public Task<AccessState> RequestAccess()
        => audioSource.RequestAccess();

    public async Task Start(SpeechRecognitionOptions? options = null)
    {
        if (IsListening)
            throw new InvalidOperationException("Speech recognition is already active. Call Stop() before starting again.");

        options ??= new SpeechRecognitionOptions();
        keywordPattern = BuildKeywordPattern(options.Keywords);
        lastKeywordFinalText = null;
        lastKeywordFinalTime = default;
        cts = new CancellationTokenSource();

        var audioStream = await audioSource.StartCaptureAsync(options.AudioProcessing, cts.Token);
        IsListening = true;
        logger.LogDebug("Audio capture started for cloud speech recognition");

        var token = cts.Token;

        // consume provider results on a background task and raise events
        recognitionTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var result in provider.RecognizeAsync(audioStream, options, token))
                {
                    ResultReceived?.Invoke(this, result);

                    if (result.IsFinal && keywordPattern != null)
                    {
                        var match = keywordPattern.Match(result.Text);
                        if (match.Success && !IsDuplicateKeywordFinal(result.Text))
                        {
                            lastKeywordFinalText = result.Text.Trim();
                            lastKeywordFinalTime = DateTime.UtcNow;
                            KeywordHeard?.Invoke(this, match.Value);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // expected on Stop()
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cloud speech recognition error");
                Error?.Invoke(this, new SpeechRecognitionError(ex.Message, ex));
            }
            finally
            {
                IsListening = false;
                await audioSource.StopCaptureAsync();
                logger.LogDebug("Audio capture stopped");
            }
        }, token);
    }

    public async Task Stop()
    {
        if (!IsListening)
            return;

        keywordPattern = null;

        if (cts != null)
            await cts.CancelAsync();

        // Wait for the recognition task to drain — critical for one-shot providers
        // (ElevenLabs Scribe) whose final result is yielded *after* cancellation fires
        // (the buffered audio is POSTed and the response arrives only then). Stop
        // returns once that result has been delivered via ResultReceived.
        if (recognitionTask != null)
        {
            try { await recognitionTask; }
            catch { /* swallow; the task surfaces errors via Error event */ }
            recognitionTask = null;
        }

        cts?.Dispose();
        cts = null;

        IsListening = false;
        logger.LogDebug("Cloud speech recognition stopped");
    }

    bool IsDuplicateKeywordFinal(string text)
    {
        if (lastKeywordFinalText == null)
            return false;
        if (!string.Equals(text.Trim(), lastKeywordFinalText, StringComparison.OrdinalIgnoreCase))
            return false;
        return DateTime.UtcNow - lastKeywordFinalTime < KeywordDedupWindow;
    }

    static Regex? BuildKeywordPattern(string[]? keywords)
    {
        if (keywords == null || keywords.Length == 0)
            return null;

        return new Regex(
            @"\b(" + string.Join("|", keywords.Select(Regex.Escape)) + @")\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );
    }
}
