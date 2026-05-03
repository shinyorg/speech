using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Android;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Speech;
using Microsoft.Extensions.Logging;
using Stream = Android.Media.Stream;

namespace Shiny.Speech;

public class SpeechToTextImpl(ActivityProvider activityProvider, ILogger<SpeechToTextImpl> logger) : ISpeechToTextService
{
    public bool IsSupported =>
        Android.Speech.SpeechRecognizer.IsRecognitionAvailable(Android.App.Application.Context);

    public async Task<AccessState> RequestAccess()
    {
        if (!IsSupported)
            return AccessState.NotSupported;

        var context = Android.App.Application.Context;
        if (context.CheckSelfPermission(Manifest.Permission.RecordAudio) == Permission.Granted)
            return AccessState.Available;

        var activity = activityProvider.Current;
        if (activity is not AndroidX.Fragment.App.FragmentActivity fragmentActivity)
            throw new InvalidOperationException("Current activity must be a FragmentActivity to request permissions");

        var fragment = new PermissionRequestFragment();
        var granted = await fragment.RequestAsync(fragmentActivity, Manifest.Permission.RecordAudio);
        return granted ? AccessState.Available : AccessState.Denied;
    }

    public async IAsyncEnumerable<SpeechRecognitionResult> ContinuousRecognize(
        SpeechRecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new SpeechRecognitionOptions();

        var channel = Channel.CreateUnbounded<SpeechRecognitionResult>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true
        });

        Android.Speech.SpeechRecognizer? recognizer = null;
        var handler = new Handler(Looper.MainLooper!);
        Intent? listenIntent = null;
        var stopped = false;

        var audioManager = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);

        var listener = new SpeechListener(channel.Writer, logger, onFinalResult: () =>
        {
            if (stopped)
            {
                channel.Writer.TryComplete();
                return;
            }

            // Mute the beep that Android plays on recognizer start/stop
            audioManager?.AdjustStreamVolume(Stream.Music, Adjust.Mute, VolumeNotificationFlags.RemoveSoundAndVibrate);

            // Android SpeechRecognizer is single-shot — restart after each final result
            handler.Post(() =>
            {
                recognizer?.StartListening(listenIntent);

                // Unmute after a short delay to allow the beep window to pass
                handler.PostDelayed(() =>
                {
                    audioManager?.AdjustStreamVolume(Stream.Music, Adjust.Unmute, VolumeNotificationFlags.RemoveSoundAndVibrate);
                }, 500);
            });
        });

        try
        {
            var tcs = new TaskCompletionSource();
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
                logger.LogDebug("Android speech recognition started");
                tcs.SetResult();
            });
            await tcs.Task;

            using var reg = cancellationToken.Register(() =>
            {
                // Set stopped BEFORE posting to handler — OnError from StopListening
                // fires synchronously on the main thread within the Post, so stopped
                // must already be true when onFinalResult checks it
                stopped = true;
                handler.Post(() =>
                {
                    recognizer?.StopListening();
                    channel.Writer.TryComplete();
                });
            });

            await foreach (var result in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                yield return result;
            }
        }
        finally
        {
            stopped = true;
            audioManager?.AdjustStreamVolume(Stream.Music, Adjust.Unmute, VolumeNotificationFlags.RemoveSoundAndVibrate);
            var r = recognizer;
            if (r != null)
            {
                handler.Post(() => r.Destroy());
            }
            logger.LogDebug("Android speech recognition stopped");
        }
    }

    public async Task<string?> ListenUntilSilence(
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string? lastText = null;
        await foreach (var result in ContinuousRecognize(options, cancellationToken))
        {
            lastText = result.Text;
            if (result.IsFinal)
                return result.Text;
        }
        return lastText;
    }

    sealed class SpeechListener(ChannelWriter<SpeechRecognitionResult> writer, ILogger logger, Action? onFinalResult = null) : Java.Lang.Object, IRecognitionListener
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

                writer.TryWrite(new SpeechRecognitionResult(text, true, confidence));
            }

            if (onFinalResult != null)
                onFinalResult();
            else
                writer.TryComplete();
        }

        public void OnPartialResults(Bundle? partialResults)
        {
            var matches = partialResults?.GetStringArrayList(Android.Speech.SpeechRecognizer.ResultsRecognition);
            var text = matches?.FirstOrDefault();
            if (!string.IsNullOrEmpty(text))
                writer.TryWrite(new SpeechRecognitionResult(text, false));
        }

        public void OnError(SpeechRecognizerError error)
        {
            logger.LogWarning("Speech recognition error: {Error}", error);
            if (error == SpeechRecognizerError.NoMatch || error == SpeechRecognizerError.SpeechTimeout)
            {
                if (onFinalResult != null)
                    onFinalResult(); // restart in continuous mode
                else
                    writer.TryComplete();
            }
            else
            {
                writer.TryComplete(new InvalidOperationException($"Speech recognition error: {error}"));
            }
        }

        public void OnReadyForSpeech(Bundle? @params) =>
            logger.LogDebug("Ready for speech");

        public void OnBeginningOfSpeech() =>
            logger.LogDebug("Beginning of speech");

        public void OnEndOfSpeech() =>
            logger.LogDebug("End of speech");

        public void OnRmsChanged(float rmsdB) { }
        public void OnBufferReceived(byte[]? buffer) { }
        public void OnEvent(int eventType, Bundle? @params) { }
    }
}
