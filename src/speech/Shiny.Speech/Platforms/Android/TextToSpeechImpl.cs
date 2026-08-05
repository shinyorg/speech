using System.Globalization;
using Android.Speech.Tts;
using Microsoft.Extensions.Logging;

namespace Shiny.Speech;

public class TextToSpeechImpl(ILogger<TextToSpeechImpl> logger) : ITextToSpeechService
{
    readonly SemaphoreSlim initLock = new(1, 1);
    Android.Speech.Tts.TextToSpeech? tts;
    bool initialized;
    TaskCompletionSource? speakTcs;

    public bool IsSupported => true;
    public bool IsSpeaking => tts?.IsSpeaking ?? false;
    public bool IsPlayerAnalysisSupported => true;
    public bool CanSynthesizeToStream => false;
    public event EventHandler<double>? AudioLevelChanged;

    public Task<Stream> SynthesizeToStreamAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("On-device text-to-speech cannot synthesize to a stream; register a cloud provider (Typecast/Azure/ElevenLabs/OpenAI).");

    async Task EnsureInitializedAsync()
    {
        if (initialized)
            return;

        await initLock.WaitAsync();
        try
        {
            if (initialized)
                return;

            var initTcs = new TaskCompletionSource();
            var initListener = new InitListener(initTcs, logger);

            // Must create TTS engine on the main thread
            new Android.OS.Handler(Android.OS.Looper.MainLooper!).Post(() =>
            {
                tts = new Android.Speech.Tts.TextToSpeech(Android.App.Application.Context, initListener);
            });

            await initTcs.Task;
            initialized = true;
        }
        finally
        {
            initLock.Release();
        }
    }

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CultureInfo? culture = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        var results = new List<VoiceInfo>();

        try
        {
            var voices = tts!.Voices;
            if (voices == null)
                return results;

            foreach (var voice in voices)
            {
                try
                {
                    var locale = voice.Locale;
                    if (locale == null)
                        continue;

                    var name = voice.Name;
                    if (name == null)
                        continue;

                    var tag = locale.ToLanguageTag();
                    if (!String.IsNullOrEmpty(tag))
                    {
                        var voiceCulture = new CultureInfo(tag);
                        if (culture == null ||
                            voiceCulture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName)
                            results.Add(new VoiceInfo(name, name, voiceCulture));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Skipping voice due to error");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error enumerating voices");
        }

        return results;
    }

    public async Task SpeakAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        await EnsureInitializedAsync();

        options ??= new TextToSpeechOptions();

        // The Android engine has no expressive control, so annotations are stripped rather than
        // spoken aloud. Any Tone on the options is discarded for the same reason.
        var spokenText = SpeechAnnotations.Resolve(text, options, SpeechToneCapabilities.None).Text;

        speakTcs = new TaskCompletionSource();
        var utteranceId = Guid.NewGuid().ToString();
        var listener = new UtteranceListener(speakTcs, logger, level => AudioLevelChanged?.Invoke(this, level));

        // TTS operations must run on main thread
        new Android.OS.Handler(Android.OS.Looper.MainLooper!).Post(() =>
        {
            try
            {
                if (options.Voice != null)
                {
                    var voices = tts!.Voices;
                    if (voices != null)
                    {
                        foreach (var v in voices)
                        {
                            if (v.Name == options.Voice.Id)
                            {
                                tts!.SetVoice(v);
                                break;
                            }
                        }
                    }
                }
                else if (options.Culture != null)
                {
                    tts!.SetLanguage(Java.Util.Locale.ForLanguageTag(options.Culture.Name));
                }

                tts!.SetSpeechRate(Math.Max(0.1f, options.SpeechRate));
                tts!.SetPitch(Math.Max(0.1f, options.Pitch));
                tts!.SetOnUtteranceProgressListener(listener);

                var bundle = new Android.OS.Bundle();
                bundle.PutFloat(Android.Speech.Tts.TextToSpeech.Engine.KeyParamVolume, Math.Clamp(options.Volume, 0f, 1f));

                tts!.Speak(spokenText, QueueMode.Flush, bundle, utteranceId);
                logger.LogDebug("Text-to-speech started");
            }
            catch (Exception ex)
            {
                speakTcs?.TrySetException(ex);
            }
        });

        await using var reg = cancellationToken.Register(() =>
        {
            new Android.OS.Handler(Android.OS.Looper.MainLooper!).Post(() => tts?.Stop());
            speakTcs?.TrySetResult();
        });

        await speakTcs.Task;
        logger.LogDebug("Text-to-speech completed");
    }

    public Task StopAsync()
    {
        if (tts?.IsSpeaking == true)
        {
            new Android.OS.Handler(Android.OS.Looper.MainLooper!).Post(() => tts?.Stop());
            speakTcs?.TrySetResult();
            logger.LogDebug("Text-to-speech stopped");
        }
        return Task.CompletedTask;
    }

    sealed class InitListener(TaskCompletionSource tcs, ILogger logger) : Java.Lang.Object, TextToSpeech.IOnInitListener
    {
        public void OnInit(OperationResult status)
        {
            if (status == OperationResult.Success)
            {
                logger.LogDebug("Android TTS engine initialized");
                tcs.TrySetResult();
            }
            else
            {
                logger.LogWarning("Android TTS engine initialization failed: {Status}", status);
                tcs.TrySetException(new InvalidOperationException($"TTS initialization failed: {status}"));
            }
        }
    }

    sealed class UtteranceListener(
        TaskCompletionSource tcs,
        ILogger logger,
        Action<double> onLevel
    ) : UtteranceProgressListener
    {
        public override void OnDone(string? utteranceId) => tcs.TrySetResult();

        public override void OnError(string? utteranceId)
        {
            logger.LogWarning("TTS utterance error: {UtteranceId}", utteranceId);
            tcs.TrySetException(new InvalidOperationException("Text-to-speech utterance failed"));
        }

        public override void OnStart(string? utteranceId)
            => logger.LogDebug("TTS utterance started: {UtteranceId}", utteranceId);

        public override void OnAudioAvailable(string? utteranceId, byte[]? audio)
        {
            if (audio == null || audio.Length < 2)
                return;

            // Android TTS delivers 16-bit signed PCM little-endian. Compute RMS into 0-1.
            double sumSquares = 0;
            var samples = audio.Length / 2;
            for (var i = 0; i < samples; i++)
            {
                var sample = (short)(audio[i * 2] | (audio[i * 2 + 1] << 8));
                var s = sample / 32768.0;
                sumSquares += s * s;
            }

            var rms = Math.Sqrt(sumSquares / samples);
            onLevel(Math.Clamp(rms, 0.0, 1.0));
        }
    }
}
