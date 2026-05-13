using System.Text.RegularExpressions;
using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using Speech;

namespace Shiny.Speech;

public class SpeechToTextImpl(ILogger<SpeechToTextImpl> logger) : ISpeechToTextService
{
    SFSpeechRecognizer? speechRecognizer;
    AVAudioEngine? audioEngine;
    SFSpeechAudioBufferRecognitionRequest? request;
    SFSpeechRecognitionTask? recognitionTask;
    CancellationTokenSource? silenceTimer;
    Regex? keywordPattern;

    public bool IsSupported =>
        SFSpeechRecognizer.AuthorizationStatus != SFSpeechRecognizerAuthorizationStatus.Restricted;

    public bool IsListening { get; private set; }

    public event EventHandler<SpeechRecognitionResult>? ResultReceived;
    public event EventHandler<string>? KeywordHeard;
    public event EventHandler<SpeechRecognitionError>? Error;

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
        if (IsListening)
            throw new InvalidOperationException("Speech recognition is already active. Call Stop() before starting again.");

        options ??= new SpeechRecognitionOptions();
        keywordPattern = BuildKeywordPattern(options.Keywords);

        var locale = options.Culture != null
            ? new NSLocale(options.Culture.Name)
            : NSLocale.CurrentLocale;

        speechRecognizer = new SFSpeechRecognizer(locale);
        if (!speechRecognizer.Available)
            throw new InvalidOperationException("Speech recognizer is not available for the requested locale.");

        audioEngine = new AVAudioEngine();
        request = new SFSpeechAudioBufferRecognitionRequest
        {
            ShouldReportPartialResults = true,
            TaskHint = SFSpeechRecognitionTaskHint.Dictation
        };

        if (options.PreferOnDevice && speechRecognizer.SupportsOnDeviceRecognition)
            request.RequiresOnDeviceRecognition = true;

        var silenceTimeout = options.SilenceTimeout;

#if !MACOS
        var audioSession = AVAudioSession.SharedInstance();
        audioSession.SetCategory(
            AVAudioSessionCategory.PlayAndRecord,
            AVAudioSessionCategoryOptions.AllowBluetooth
                | AVAudioSessionCategoryOptions.DefaultToSpeaker
                | AVAudioSessionCategoryOptions.AllowBluetoothA2DP,
            out var categoryError
        );
        if (categoryError != null)
            throw new InvalidOperationException($"Failed to set audio session category: {categoryError.LocalizedDescription}");

        audioSession.SetActive(true, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var activeError);
        if (activeError != null)
            throw new InvalidOperationException($"Failed to activate audio session: {activeError.LocalizedDescription}");
#endif

        var inputNode = audioEngine.InputNode;
        var recordingFormat = inputNode.GetBusOutputFormat(0);

        recognitionTask = speechRecognizer.GetRecognitionTask(request, (result, error) =>
        {
            if (error != null)
            {
                // "No speech detected" and "retry" are normal end-of-recognition conditions, not fatal errors
                if (error.Code == 203 || error.Code == 216 || error.Code == 1110)
                {
                    logger.LogDebug("Speech recognition ended: {Error}", error.LocalizedDescription);
                }
                else
                {
                    logger.LogError("Speech recognition error: {Error}", error.LocalizedDescription);
                    Error?.Invoke(this, new SpeechRecognitionError(
                        error.LocalizedDescription,
                        new InvalidOperationException(error.LocalizedDescription)
                    ));
                }
                return;
            }

            if (result == null)
                return;

            var text = result.BestTranscription.FormattedString;
            var isFinal = result.Final;

            float? confidence = null;
            var segments = result.BestTranscription.Segments;
            if (segments.Length > 0)
                confidence = (float)segments[^1].Confidence;

            var speechResult = new SpeechRecognitionResult(text, isFinal, confidence);
            ResultReceived?.Invoke(this, speechResult);

            if (isFinal && keywordPattern != null)
            {
                var match = keywordPattern.Match(text);
                if (match.Success)
                    KeywordHeard?.Invoke(this, match.Value);
            }

            if (!isFinal)
                ResetSilenceTimer(silenceTimeout);
        });

        inputNode.InstallTapOnBus(0, 1024, recordingFormat, (buffer, when) =>
        {
            request.Append(buffer);
        });

        audioEngine.Prepare();
        audioEngine.StartAndReturnError(out var engineError);
        if (engineError != null)
            throw new InvalidOperationException($"Failed to start audio engine: {engineError.LocalizedDescription}");

        IsListening = true;
        logger.LogDebug("Speech recognition started");
        ResetSilenceTimer(silenceTimeout);

        return Task.CompletedTask;
    }

    public Task Stop()
    {
        if (!IsListening)
            return Task.CompletedTask;

        IsListening = false;
        silenceTimer?.Cancel();
        silenceTimer?.Dispose();
        silenceTimer = null;
        keywordPattern = null;

        if (audioEngine?.Running == true)
        {
            audioEngine.Stop();
            audioEngine.InputNode.RemoveTapOnBus(0);
        }

        recognitionTask?.Cancel();
        recognitionTask = null;
        request = null;
        audioEngine = null;
        speechRecognizer = null;

#if !MACOS
        var session = AVAudioSession.SharedInstance();
        session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
#endif

        logger.LogDebug("Speech recognition stopped");
        return Task.CompletedTask;
    }

    void ResetSilenceTimer(TimeSpan silenceTimeout)
    {
        var old = silenceTimer;
        silenceTimer = new CancellationTokenSource();
        try { old?.Cancel(); } catch (ObjectDisposedException) { }
        old?.Dispose();
        var token = silenceTimer.Token;
        _ = Task.Delay(silenceTimeout, token).ContinueWith(_ =>
        {
            logger.LogDebug("Silence timeout reached, ending audio");
            request?.EndAudio();
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
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
