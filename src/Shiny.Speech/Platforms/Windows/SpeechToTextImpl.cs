using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace Shiny.Speech;

public class SpeechToTextImpl(ILogger<SpeechToTextImpl> logger) : ISpeechToTextService
{
    Windows.Media.SpeechRecognition.SpeechRecognizer? recognizer;
    Regex? keywordPattern;

    public bool IsSupported => true;
    public bool IsListening { get; private set; }

    public event EventHandler<SpeechRecognitionResult>? ResultReceived;
    public event EventHandler<string>? KeywordHeard;
    public event EventHandler<SpeechRecognitionError>? Error;

    public async Task<AccessState> RequestAccess()
    {
        try
        {
            using var r = new Windows.Media.SpeechRecognition.SpeechRecognizer();
            await r.CompileConstraintsAsync();
            return AccessState.Available;
        }
        catch (UnauthorizedAccessException)
        {
            return AccessState.Denied;
        }
        catch (Exception)
        {
            return AccessState.NotSupported;
        }
    }

    public async Task Start(SpeechRecognitionOptions? options = null)
    {
        if (IsListening)
            throw new InvalidOperationException("Speech recognition is already active. Call Stop() before starting again.");

        options ??= new SpeechRecognitionOptions();
        keywordPattern = BuildKeywordPattern(options.Keywords);

        recognizer = options.Culture != null
            ? new Windows.Media.SpeechRecognition.SpeechRecognizer(new Language(options.Culture.Name))
            : new Windows.Media.SpeechRecognition.SpeechRecognizer();

        recognizer.Timeouts.EndSilenceTimeout = options.SilenceTimeout;
        recognizer.Timeouts.BabbleTimeout = TimeSpan.FromSeconds(0);

        var compilationResult = await recognizer.CompileConstraintsAsync();
        if (compilationResult.Status != SpeechRecognitionResultStatus.Success)
            throw new InvalidOperationException($"Speech recognizer compilation failed: {compilationResult.Status}");

        recognizer.ContinuousRecognitionSession.ResultGenerated += (_, args) =>
        {
            if (args.Result.Status != SpeechRecognitionResultStatus.Success)
                return;

            var confidence = (float)args.Result.RawConfidence;
            var result = new SpeechRecognitionResult(args.Result.Text, true, confidence);
            ResultReceived?.Invoke(this, result);

            if (keywordPattern != null)
            {
                var match = keywordPattern.Match(args.Result.Text);
                if (match.Success)
                    KeywordHeard?.Invoke(this, match.Value);
            }
        };

        recognizer.HypothesisGenerated += (_, args) =>
        {
            ResultReceived?.Invoke(this, new SpeechRecognitionResult(args.Hypothesis.Text, false));
        };

        recognizer.ContinuousRecognitionSession.Completed += (_, args) =>
        {
            logger.LogDebug("Windows continuous recognition completed: {Status}", args.Status);
            if (args.Status != SpeechRecognitionResultStatus.Success)
            {
                Error?.Invoke(this, new SpeechRecognitionError(
                    $"Recognition completed with status: {args.Status}"
                ));
            }
        };

        await recognizer.ContinuousRecognitionSession.StartAsync();
        IsListening = true;
        logger.LogDebug("Windows speech recognition started");
    }

    public async Task Stop()
    {
        if (!IsListening)
            return;

        IsListening = false;
        keywordPattern = null;

        if (recognizer != null)
        {
            try
            {
                await recognizer.ContinuousRecognitionSession.StopAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error stopping speech recognition");
            }
            finally
            {
                recognizer.Dispose();
                recognizer = null;
            }
        }

        logger.LogDebug("Windows speech recognition stopped");
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
