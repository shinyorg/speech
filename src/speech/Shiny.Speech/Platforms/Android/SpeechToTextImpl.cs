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

    // Consecutive recoverable errors since the last result. Continuous mode re-arms after a
    // failure instead of dying quietly, but backs off as the failures stack up and gives up
    // once SpeechRetryPolicy says the problem is not transient.
    int consecutiveErrors;

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
        consecutiveErrors = 0;

        // The platform recognizer is a separate service that opens the microphone itself, so there
        // is no capture session here to apply effects to. Say so rather than silently dropping it.
        if (options.AudioProcessing != null)
        {
            logger.LogWarning(
                "SpeechRecognitionOptions.AudioProcessing is ignored on Android - the platform SpeechRecognizer owns its own microphone capture and exposes no voice-processing controls. Use a cloud provider (which captures through IAudioSource) if the recognition path needs echo cancellation or noise suppression."
            );
        }

        var tcs = new TaskCompletionSource();
        handler = new Handler(Looper.MainLooper!);
        audioManager = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);

        var listener = new SpeechListener(logger,
            onResult: result =>
            {
                // A result means the loop is healthy again - forgive whatever failed before it.
                consecutiveErrors = 0;
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
            onRecognizerError: HandleRecognizerError,
            onRms: rmsDb => InputLevelChanged?.Invoke(this, NormalizeRmsDb(rmsDb)),
            // Android SpeechRecognizer is single-shot - restart after each final result
            onFinalResult: () => RestartListening(TimeSpan.Zero)
        );

        handler.Post(() =>
        {
            recognizer = CreateRecognizer(options.PreferOnDevice);
            recognizer.SetRecognitionListener(listener);

            listenIntent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
            listenIntent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
            listenIntent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
            listenIntent.PutExtra(RecognizerIntent.ExtraMaxResults, 1);

            // Keeps the default (network-backed) recognizer off the network too, for the case
            // where no dedicated on-device recognition service is installed.
            if (options.PreferOnDevice)
                listenIntent.PutExtra(RecognizerIntent.ExtraPreferOffline, true);

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

    /// <summary>
    /// On-device recognition is a separate recognizer, not an extra on the default one. Falls back
    /// to the system recognizer when the device has no on-device service installed - the
    /// EXTRA_PREFER_OFFLINE hint on the intent still asks that one to stay local where it can.
    /// </summary>
    Android.Speech.SpeechRecognizer CreateRecognizer(bool preferOnDevice)
    {
        var context = Android.App.Application.Context;

        if (preferOnDevice && OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            if (Android.Speech.SpeechRecognizer.IsOnDeviceRecognitionAvailable(context))
            {
                logger.LogDebug("Using the Android on-device speech recognizer");
                return Android.Speech.SpeechRecognizer.CreateOnDeviceSpeechRecognizer(context);
            }
            logger.LogDebug("On-device recognition was preferred but is not available - falling back to the system recognizer");
        }

        return Android.Speech.SpeechRecognizer.CreateSpeechRecognizer(context)!;
    }

    void HandleRecognizerError(SpeechRecognizerError error)
    {
        if (!IsListening)
            return;

        // Nobody spoke. In continuous mode that is the shape of silence, not a failure.
        if (error is SpeechRecognizerError.NoMatch or SpeechRecognizerError.SpeechTimeout)
        {
            RestartListening(TimeSpan.Zero);
            return;
        }

        var message = $"Speech recognition error: {error}";
        Error?.Invoke(this, new SpeechRecognitionError(message, new InvalidOperationException(message)));

        if (IsFatal(error))
        {
            logger.LogError("Speech recognition stopped - {Error} cannot be recovered from by retrying", error);
            _ = this.Stop();
            return;
        }

        consecutiveErrors++;

        if (SpeechRetryPolicy.ShouldGiveUp(consecutiveErrors))
        {
            logger.LogError(
                "Speech recognition stopped after {Count} consecutive errors, the last being {Error}",
                consecutiveErrors,
                error
            );
            _ = this.Stop();
            return;
        }

        var backoff = SpeechRetryPolicy.GetBackoff(consecutiveErrors);
        logger.LogWarning(
            "Re-arming speech recognition in {Backoff} after {Error} (attempt {Count})",
            backoff,
            error,
            consecutiveErrors
        );
        RestartListening(backoff);
    }

    /// <summary>
    /// Nothing retryable about these - the app is missing a permission, or the language will never
    /// resolve. Retrying just burns battery until the caller notices nothing is being recognized.
    /// </summary>
    static bool IsFatal(SpeechRecognizerError error)
    {
        if (error == SpeechRecognizerError.InsufficientPermissions)
            return true;

        // The language errors were only added in API 31, so older devices never raise them.
        return OperatingSystem.IsAndroidVersionAtLeast(31)
            && error is SpeechRecognizerError.LanguageNotSupported or SpeechRecognizerError.LanguageUnavailable;
    }

    void RestartListening(TimeSpan delay)
    {
        if (!IsListening)
            return;

        // Read once - Stop() clears the field, and the posted callback must not resurrect a
        // recognizer that has already been destroyed.
        var h = handler;
        if (h == null)
            return;

        h.PostDelayed(() =>
        {
            if (!IsListening)
                return;

            // Mute the beep that Android plays on recognizer start. Muted here rather than before
            // the delay so a multi-second backoff does not silence the app's audio while it waits.
            audioManager?.AdjustStreamVolume(Stream.Music, Adjust.Mute, VolumeNotificationFlags.RemoveSoundAndVibrate);
            recognizer?.StartListening(listenIntent);

            // Unmute after a short delay to allow the beep window to pass
            handler?.PostDelayed(
                () => audioManager?.AdjustStreamVolume(Stream.Music, Adjust.Unmute, VolumeNotificationFlags.RemoveSoundAndVibrate),
                500
            );
        }, (long)delay.TotalMilliseconds);
    }

    public Task Stop()
    {
        if (!IsListening)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource();
        IsListening = false;
        audioManager?.AdjustStreamVolume(Stream.Music, Adjust.Unmute, VolumeNotificationFlags.RemoveSoundAndVibrate);

        var r = recognizer;
        // Clear the handler before posting the teardown, so a restart racing with Stop() sees a
        // null handler and bails instead of starting a recognizer we are about to destroy.
        var h = handler;
        handler = null;
        recognizer = null;
        listenIntent = null;
        keywordPattern = null;

        if (r != null && h != null)
        {
            h.Post(() =>
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
        Action<SpeechRecognizerError> onRecognizerError,
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

        // Classification lives on the service, which owns the retry state and can stop the session.
        public void OnError(SpeechRecognizerError error)
        {
            logger.LogWarning("Speech recognition error: {Error}", error);
            onRecognizerError(error);
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
