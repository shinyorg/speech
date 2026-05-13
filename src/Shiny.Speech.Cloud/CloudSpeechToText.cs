using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Shiny.Speech;

namespace Shiny.Speech.Cloud;

/// <summary>
/// ISpeechToTextService implementation that captures audio from the platform microphone
/// and delegates recognition to a pluggable ISpeechToTextProvider (Azure, Google, etc.).
/// </summary>
public class CloudSpeechToText(
    ISpeechToTextProvider provider,
    IAudioSource audioSource,
    ILogger<CloudSpeechToText> logger
) : ISpeechToTextService
{
    CancellationTokenSource? cts;
    Regex? keywordPattern;

    public bool IsSupported => true;
    public bool IsListening { get; private set; }

    public event EventHandler<SpeechRecognitionResult>? ResultReceived;
    public event EventHandler<string>? KeywordHeard;
    public event EventHandler<SpeechRecognitionError>? Error;

    public Task<AccessState> RequestAccess()
        => Task.FromResult(AccessState.Available);

    public async Task Start(SpeechRecognitionOptions? options = null)
    {
        if (IsListening)
            throw new InvalidOperationException("Speech recognition is already active. Call Stop() before starting again.");

        options ??= new SpeechRecognitionOptions();
        keywordPattern = BuildKeywordPattern(options.Keywords);
        cts = new CancellationTokenSource();

        var audioStream = await audioSource.StartCaptureAsync(cts.Token);
        IsListening = true;
        logger.LogDebug("Audio capture started for cloud speech recognition");

        var token = cts.Token;

        // consume provider results on a background task and raise events
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var result in provider.RecognizeAsync(audioStream, options, token))
                {
                    ResultReceived?.Invoke(this, result);

                    if (result.IsFinal && keywordPattern != null)
                    {
                        var match = keywordPattern.Match(result.Text);
                        if (match.Success)
                            KeywordHeard?.Invoke(this, match.Value);
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
        {
            await cts.CancelAsync();
            cts.Dispose();
            cts = null;
        }

        IsListening = false;
        logger.LogDebug("Cloud speech recognition stopped");
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
