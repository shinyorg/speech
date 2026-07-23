using System.Text.RegularExpressions;
using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using Speech;
using Shiny.Audio;

namespace Shiny.Speech;

public class SpeechToTextImpl(ILogger<SpeechToTextImpl> logger) : ISpeechToTextService
{
    readonly object stateLock = new();

    SFSpeechRecognizer? speechRecognizer;
    AVAudioEngine? audioEngine;
    SFSpeechAudioBufferRecognitionRequest? request;
    SFSpeechRecognitionTask? recognitionTask;
    CancellationTokenSource? silenceTimer;
    Regex? keywordPattern;
    TimeSpan silenceTimeout;
    bool preferOnDevice;

    // Dedup state — suppress KeywordHeard re-fires from trailing-audio carry-over.
    // SFSpeechRecognizer can deliver a final result with the previous best-guess text
    // when a re-armed task receives only silence or the tail of the prior utterance.
    string? lastKeywordFinalText;
    DateTime lastKeywordFinalTime;
    static readonly TimeSpan KeywordDedupWindow = TimeSpan.FromSeconds(3);

    public bool IsSupported =>
        SFSpeechRecognizer.AuthorizationStatus != SFSpeechRecognizerAuthorizationStatus.Restricted;

    public bool IsListening { get; private set; }
    public bool IsInputAnalysisSupported => true;

    public event EventHandler<SpeechRecognitionResult>? ResultReceived;
    public event EventHandler<string>? KeywordHeard;
    public event EventHandler<SpeechRecognitionError>? Error;
    public event EventHandler<double>? InputLevelChanged;

    public Task<AccessState> RequestAccess()
    {
        var tcs = new TaskCompletionSource<AccessState>();

        SFSpeechRecognizer.RequestAuthorization(status =>
        {
            switch (status)
            {
                case SFSpeechRecognizerAuthorizationStatus.Authorized:
#if MACOS
                    tcs.TrySetResult(AccessState.Available);
#else
                    var audioSession = AVAudioSession.SharedInstance();
                    audioSession.RequestRecordPermission(granted =>
                    {
                        tcs.TrySetResult(granted ? AccessState.Available : AccessState.Denied);
                    });
#endif
                    break;

                case SFSpeechRecognizerAuthorizationStatus.Denied:
                    tcs.TrySetResult(AccessState.Denied);
                    break;

                case SFSpeechRecognizerAuthorizationStatus.Restricted:
                    tcs.TrySetResult(AccessState.Restricted);
                    break;

                default:
                    tcs.TrySetResult(AccessState.Unknown);
                    break;
            }
        });

        return tcs.Task;
    }

    public Task Start(SpeechRecognitionOptions? options = null)
    {
        lock (this.stateLock)
        {
            if (this.IsListening)
                throw new InvalidOperationException("Speech recognition is already active. Call Stop() before starting again.");

            options ??= new SpeechRecognitionOptions();
            this.keywordPattern = BuildKeywordPattern(options.Keywords);
            this.silenceTimeout = options.SilenceTimeout;
            this.preferOnDevice = options.PreferOnDevice;
            this.lastKeywordFinalText = null;
            this.lastKeywordFinalTime = default;

            var locale = options.Culture != null
                ? new NSLocale(options.Culture.Name)
                : NSLocale.CurrentLocale;

            this.speechRecognizer = new SFSpeechRecognizer(locale);
            if (!this.speechRecognizer.Available)
                throw new InvalidOperationException("Speech recognizer is not available for the requested locale.");

            this.audioEngine = new AVAudioEngine();

#if !MACOS
            var audioSession = AVAudioSession.SharedInstance();
            audioSession.SetCategory(
                AVAudioSessionCategory.PlayAndRecord,
                AVAudioSessionCategoryOptions.AllowBluetooth
                    | AVAudioSessionCategoryOptions.AllowBluetoothA2DP
                    | AVAudioSessionCategoryOptions.DefaultToSpeaker,
                out var categoryError
            );
            if (categoryError != null)
                throw new InvalidOperationException($"Failed to set audio session category: {categoryError.LocalizedDescription}");

            audioSession.SetMode(AVAudioSessionMode.VoiceChat.GetConstant()!, out var modeError);
            if (modeError != null)
                logger.LogWarning("Failed to set audio session mode to VoiceChat: {Error}", modeError.LocalizedDescription);

            audioSession.SetActive(true, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var activeError);
            if (activeError != null)
                throw new InvalidOperationException($"Failed to activate audio session: {activeError.LocalizedDescription}");
#endif

            var inputNode = this.audioEngine.InputNode;
            var recordingFormat = inputNode.GetBusOutputFormat(0);

            // Install the tap once for the lifetime of the session. The audio engine + tap stay
            // alive across recognition-task re-arms; each new buffer is routed to whatever the
            // current `request` is at the moment the tap fires, so the mic stays open even after
            // SFSpeechRecognitionTask completes a single utterance.
            var levelThrottle = new AudioLevelThrottle();

            inputNode.InstallTapOnBus(0, 1024, recordingFormat, (buffer, when) =>
            {
                // The tap already has the mic buffers the recognizer consumes, so metering here
                // costs nothing extra and works whether recognition is on-device or server-side.
                if (levelThrottle.TryEmit(AppleAudioLevel.FromBuffer(buffer), out var level))
                    this.InputLevelChanged?.Invoke(this, level);

                try { this.request?.Append(buffer); }
                catch (Exception ex) { logger.LogDebug(ex, "Ignored buffer-append during request swap"); }
            });

            this.audioEngine.Prepare();
            this.audioEngine.StartAndReturnError(out var engineError);
            if (engineError != null)
                throw new InvalidOperationException($"Failed to start audio engine: {engineError.LocalizedDescription}");

            this.IsListening = true;
            this.StartRecognitionTaskLocked();
        }

        logger.LogDebug("Speech recognition started");
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        lock (this.stateLock)
        {
            if (!this.IsListening)
                return Task.CompletedTask;

            this.IsListening = false;
            this.silenceTimer?.Cancel();
            this.silenceTimer?.Dispose();
            this.silenceTimer = null;
            this.keywordPattern = null;

            if (this.audioEngine?.Running == true)
            {
                this.audioEngine.Stop();
                this.audioEngine.InputNode.RemoveTapOnBus(0);
            }

            this.recognitionTask?.Cancel();
            this.recognitionTask = null;
            this.request = null;
            this.audioEngine = null;
            this.speechRecognizer = null;

#if !MACOS
            var session = AVAudioSession.SharedInstance();
            session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
#endif
        }

        logger.LogDebug("Speech recognition stopped");
        return Task.CompletedTask;
    }

    void StartRecognitionTaskLocked()
    {
        // Caller must hold stateLock.
        if (this.speechRecognizer == null)
            return;

        var newRequest = new SFSpeechAudioBufferRecognitionRequest
        {
            ShouldReportPartialResults = true,
            TaskHint = SFSpeechRecognitionTaskHint.Dictation
        };
        if (this.preferOnDevice && this.speechRecognizer.SupportsOnDeviceRecognition)
            newRequest.RequiresOnDeviceRecognition = true;

        this.request = newRequest;
        this.recognitionTask = this.speechRecognizer.GetRecognitionTask(newRequest, this.HandleRecognition);
        this.ResetSilenceTimer(this.silenceTimeout);
    }

    void HandleRecognition(SFSpeechRecognitionResult? result, NSError? error)
    {
        var shouldReArm = false;

        if (error != null)
        {
            // 203 = "No speech detected"; 216 = "Retry"; 1110 = recoverable session end.
            // These are normal end-of-task conditions on iOS — re-arm to keep the mic open.
            if (error.Code == 203 || error.Code == 216 || error.Code == 1110)
            {
                logger.LogDebug("Speech recognition task ended: {Error}", error.LocalizedDescription);
                shouldReArm = true;
            }
            else
            {
                logger.LogError("Speech recognition error: {Error}", error.LocalizedDescription);
                Error?.Invoke(this, new SpeechRecognitionError(
                    error.LocalizedDescription,
                    new InvalidOperationException(error.LocalizedDescription)
                ));
                return;
            }
        }
        else if (result != null)
        {
            var text = result.BestTranscription.FormattedString;
            var isFinal = result.Final;

            float? confidence = null;
            var segments = result.BestTranscription.Segments;
            if (segments.Length > 0)
                confidence = (float)segments[^1].Confidence;

            var speechResult = new SpeechRecognitionResult(text, isFinal, confidence);
            ResultReceived?.Invoke(this, speechResult);

            if (isFinal && this.keywordPattern != null)
            {
                var match = this.keywordPattern.Match(text);
                if (match.Success && !IsDuplicateKeywordFinal(text))
                {
                    this.lastKeywordFinalText = text.Trim();
                    this.lastKeywordFinalTime = DateTime.UtcNow;
                    KeywordHeard?.Invoke(this, match.Value);
                }
            }

            if (isFinal)
                shouldReArm = true;
            else
                this.ResetSilenceTimer(this.silenceTimeout);
        }
        else
        {
            return;
        }

        if (!shouldReArm)
            return;

        lock (this.stateLock)
        {
            if (!this.IsListening)
                return;

            this.StartRecognitionTaskLocked();
        }
        logger.LogDebug("Recognition task re-armed (keep-mic-open)");
    }

    void ResetSilenceTimer(TimeSpan timeout)
    {
        var old = this.silenceTimer;
        this.silenceTimer = new CancellationTokenSource();
        try { old?.Cancel(); } catch (ObjectDisposedException) { }
        old?.Dispose();
        var token = this.silenceTimer.Token;
        _ = Task.Delay(timeout, token).ContinueWith(_ =>
        {
            logger.LogDebug("Silence timeout reached, ending audio");
            this.request?.EndAudio();
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    bool IsDuplicateKeywordFinal(string text)
    {
        if (this.lastKeywordFinalText == null)
            return false;
        if (!string.Equals(text.Trim(), this.lastKeywordFinalText, StringComparison.OrdinalIgnoreCase))
            return false;
        return DateTime.UtcNow - this.lastKeywordFinalTime < KeywordDedupWindow;
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
