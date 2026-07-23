using System.Text.RegularExpressions;
using Android;
using Android.Content;
using Android.Media;
using Android.OS;
using Android.Speech;
using Microsoft.Extensions.Logging;
using Stream = Android.Media.Stream;

namespace Shiny.Speech;

public class SpeechToTextImpl(AndroidPlatform platform, ILogger<SpeechToTextImpl> logger) : ISpeechToTextService
{
    Android.Speech.SpeechRecognizer? recognizer;
    Handler? handler;
    Intent? listenIntent;
    Regex? keywordPattern;
    AudioManager? audioManager;

    // Dedup state — Android SpeechRecognizer is single-shot, and the StartListening
    // restart cycle can produce a final result echoing the prior utterance when the
    // user is actually silent. Suppress same-text re-fires within a short window.
    string? lastKeywordFinalText;
    DateTime lastKeywordFinalTime;
    static readonly TimeSpan KeywordDedupWindow = TimeSpan.FromSeconds(3);

    public bool IsSupported =>
        Android.Speech.SpeechRecognizer.IsRecognitionAvailable(Android.App.Application.Context);

    public bool IsListening { get; private set; }
    public bool IsInputAnalysisSupported => true;

    public event EventHandler<SpeechRecognitionResult>? ResultReceived;
    public event EventHandler<string>? KeywordHeard;
    public event EventHandler<SpeechRecognitionError>? Error;
    public event EventHandler<double>? InputLevelChanged;

    public Task<AccessState> RequestAccess()
    {
        if (!IsSupported)
            return Task.FromResult(AccessState.NotSupported);

        return platform.RequestAccess(Manifest.Permission.RecordAudio);
    }

    public Task Start(SpeechRecognitionOptions? options = null)
    {
        if (IsListening)
            throw new InvalidOperationException("Speech recognition is already active. Call Stop() before starting again.");

        options ??= new SpeechRecognitionOptions();
        keywordPattern = BuildKeywordPattern(options.Keywords);
        lastKeywordFinalText = null;
        lastKeywordFinalTime = default;

        var tcs = new TaskCompletionSource();
        handler = new Handler(Looper.MainLooper!);
        audioManager = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);

        var listener = new SpeechListener(logger,
            onResult: result =>
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
            },
            onError: error =>
            {
                Error?.Invoke(this, error);
            },
            onRms: rmsDb => InputLevelChanged?.Invoke(this, NormalizeRmsDb(rmsDb)),
            onFinalResult: () =>
            {
                if (!IsListening)
                    return;

                // Mute the beep that Android plays on recognizer start/stop
                audioManager?.AdjustStreamVolume(Stream.Music, Adjust.Mute, VolumeNotificationFlags.RemoveSoundAndVibrate);

                // Android SpeechRecognizer is single-shot - restart after each final result
                handler?.Post(() =>
                {
                    recognizer?.StartListening(listenIntent);

                    // Unmute after a short delay to allow the beep window to pass
                    handler?.PostDelayed(() =>
                    {
                        audioManager?.AdjustStreamVolume(Stream.Music, Adjust.Unmute, VolumeNotificationFlags.RemoveSoundAndVibrate);
                    }, 500);
                });
            }
        );

        handler.Post(() =>
        {
            recognizer = Android.Speech.SpeechRecognizer.CreateSpeechRecognizer(Android.App.Application.Context);
            recognizer.SetRecognitionListener(listener);

            listenIntent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
            listenIntent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
            listenIntent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
            listenIntent.PutExtra(RecognizerIntent.ExtraMaxResults, 1);

            if (options.Culture != null)
                listenIntent.PutExtra(RecognizerIntent.ExtraLanguage, options.Culture.Name);

            var silenceMs = (long)options.SilenceTimeout.TotalMilliseconds;
            listenIntent.PutExtra(RecognizerIntent.ExtraSpeechInputCompleteSilenceLengthMillis, silenceMs);
            listenIntent.PutExtra(RecognizerIntent.ExtraSpeechInputPossiblyCompleteSilenceLengthMillis, silenceMs);
            listenIntent.PutExtra(RecognizerIntent.ExtraSpeechInputMinimumLengthMillis, silenceMs);

            recognizer.StartListening(listenIntent);
            IsListening = true;
            logger.LogDebug("Android speech recognition started");
            tcs.SetResult();
        });

        return tcs.Task;
    }

    public Task Stop()
    {
        if (!IsListening)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource();
        IsListening = false;
        audioManager?.AdjustStreamVolume(Stream.Music, Adjust.Unmute, VolumeNotificationFlags.RemoveSoundAndVibrate);

        var r = recognizer;
        recognizer = null;
        listenIntent = null;
        keywordPattern = null;

        if (r != null && handler != null)
        {
            handler.Post(() =>
            {
                r.StopListening();
                r.Destroy();
                tcs.SetResult();
            });
        }
        else
        {
            tcs.SetResult();
        }

        handler = null;
        audioManager = null;
        logger.LogDebug("Android speech recognition stopped");
        return tcs.Task;
    }

    // SpeechRecognizer reports a level in dB on its own scale — roughly -2 (silence) to 10 (loud),
    // not dBFS — so it can't go through AudioLevel. Clamp that window into the shared 0–1 range.
    const float MinRmsDb = -2f;
    const float MaxRmsDb = 10f;

    static double NormalizeRmsDb(float rmsDb)
        => Math.Clamp((rmsDb - MinRmsDb) / (MaxRmsDb - MinRmsDb), 0f, 1f);

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

    sealed class SpeechListener(
        ILogger logger,
        Action<SpeechRecognitionResult> onResult,
        Action<SpeechRecognitionError> onError,
        Action<float>? onRms = null,
        Action? onFinalResult = null
    ) : Java.Lang.Object, IRecognitionListener
    {
        public void OnResults(Bundle? results)
        {
            var matches = results?.GetStringArrayList(Android.Speech.SpeechRecognizer.ResultsRecognition);
            var text = matches?.FirstOrDefault();
            if (text != null)
            {
                float? confidence = null;
                var scores = results?.GetFloatArray(Android.Speech.SpeechRecognizer.ConfidenceScores);
                if (scores is { Length: > 0 })
                    confidence = scores[0];

                onResult(new SpeechRecognitionResult(text, true, confidence));
            }

            onFinalResult?.Invoke();
        }

        public void OnPartialResults(Bundle? partialResults)
        {
            var matches = partialResults?.GetStringArrayList(Android.Speech.SpeechRecognizer.ResultsRecognition);
            var text = matches?.FirstOrDefault();
            if (!string.IsNullOrEmpty(text))
                onResult(new SpeechRecognitionResult(text, false));
        }

        public void OnError(SpeechRecognizerError error)
        {
            logger.LogWarning("Speech recognition error: {Error}", error);
            if (error == SpeechRecognizerError.NoMatch || error == SpeechRecognizerError.SpeechTimeout)
            {
                onFinalResult?.Invoke(); // restart in continuous mode
            }
            else
            {
                onError(new SpeechRecognitionError(
                    $"Speech recognition error: {error}",
                    new InvalidOperationException($"Speech recognition error: {error}")
                ));
            }
        }

        public void OnReadyForSpeech(Bundle? @params) =>
            logger.LogDebug("Ready for speech");

        public void OnBeginningOfSpeech() =>
            logger.LogDebug("Beginning of speech");

        public void OnEndOfSpeech() =>
            logger.LogDebug("End of speech");

        public void OnRmsChanged(float rmsdB) => onRms?.Invoke(rmsdB);
        public void OnBufferReceived(byte[]? buffer) { }
        public void OnEvent(int eventType, Bundle? @params) { }
    }
}
