using System.Globalization;
using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;

namespace Shiny.Speech;

public class TextToSpeechImpl(ILogger<TextToSpeechImpl> logger) : ITextToSpeechService, IDisposable
{
    readonly AVSpeechSynthesizer synthesizer = new();

    AVAudioEngine? engine;
    AVAudioPlayerNode? playerNode;
    AVAudioFormat? connectedFormat;
    readonly object engineLock = new();
    TaskCompletionSource? writeTcs;
    int scheduledBuffers;
    bool writeCompleted;

    public bool IsSupported => true;
    public bool IsSpeaking => (playerNode?.Playing ?? false) || synthesizer.Speaking;
    public bool IsPlayerAnalysisSupported => true;
    public event EventHandler<double>? AudioLevelChanged;

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CultureInfo? culture = null, CancellationToken cancellationToken = default)
    {
        var voices = AVSpeechSynthesisVoice.GetSpeechVoices();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<VoiceInfo>();

        foreach (var voice in voices)
        {
            var voiceCulture = new CultureInfo(voice.Language);
            if (culture != null && !voiceCulture.Name.StartsWith(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Deduplicate by name + language (Apple returns multiple quality variants per voice)
            var key = $"{voice.Name}|{voice.Language}";
            if (!seen.Add(key))
                continue;

            results.Add(new VoiceInfo(voice.Identifier, voice.Name, voiceCulture));
        }

        return Task.FromResult<IReadOnlyList<VoiceInfo>>(results);
    }

    public async Task SpeakAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        options ??= new TextToSpeechOptions();

        var utterance = BuildUtterance(text, options);
        ConfigureAudioSession();

        var tcs = new TaskCompletionSource();
        writeTcs = tcs;
        scheduledBuffers = 0;
        writeCompleted = false;

        using var reg = cancellationToken.Register(() =>
        {
            try { playerNode?.Stop(); } catch { /* ignore */ }
            tcs.TrySetResult();
        });

        synthesizer.WriteUtterance(utterance, buffer =>
        {
            try
            {
                HandleSynthesizerBuffer(buffer);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed handling TTS buffer");
                writeTcs?.TrySetException(ex);
            }
        });

        logger.LogDebug("Text-to-speech started");
        await tcs.Task;
        logger.LogDebug("Text-to-speech completed");
        writeTcs = null;
    }

    AVSpeechUtterance BuildUtterance(string text, TextToSpeechOptions options)
    {
        var utterance = new AVSpeechUtterance(text);

        if (options.Voice != null)
            utterance.Voice = AVSpeechSynthesisVoice.FromIdentifier(options.Voice.Id);
        else if (options.Culture != null)
            utterance.Voice = AVSpeechSynthesisVoice.FromLanguage(options.Culture.Name);

        utterance.Rate = Math.Clamp(options.SpeechRate * AVSpeechUtterance.DefaultSpeechRate, AVSpeechUtterance.MinimumSpeechRate, AVSpeechUtterance.MaximumSpeechRate);
        utterance.PitchMultiplier = Math.Clamp(options.Pitch, 0.5f, 2.0f);
        utterance.Volume = Math.Clamp(options.Volume, 0f, 1f);
        return utterance;
    }

    void ConfigureAudioSession()
    {
#if !MACOS
        // If STT (or another component) has already configured the session for input+output
        // — i.e. PlayAndRecord — leave the category alone. Switching to Playback-only would
        // suspend the microphone for the duration of TTS and break any concurrent interruption
        // listening. Always reactivate the session in case the previous owner deactivated it.
        var audioSession = AVAudioSession.SharedInstance();
        var playAndRecord = AVAudioSessionCategory.PlayAndRecord.GetConstant();
        if (audioSession.Category != playAndRecord)
            audioSession.SetCategory(AVAudioSessionCategory.Playback, (AVAudioSessionCategoryOptions)0, out _);
        audioSession.SetActive(true, out _);
#endif
    }

    void HandleSynthesizerBuffer(AVAudioBuffer buffer)
    {
        if (buffer is not AVAudioPcmBuffer pcm)
            return;

        if (pcm.FrameLength == 0)
        {
            writeCompleted = true;
            if (Volatile.Read(ref scheduledBuffers) == 0)
                writeTcs?.TrySetResult();
            return;
        }

        EnsureEngineConnected(pcm.Format);

        var node = playerNode;
        if (node == null)
            return;

        Interlocked.Increment(ref scheduledBuffers);
        node.ScheduleBuffer(pcm, () =>
        {
            var remaining = Interlocked.Decrement(ref scheduledBuffers);
            if (writeCompleted && remaining == 0)
                writeTcs?.TrySetResult();
        });

        if (!node.Playing)
            node.Play();
    }

    void EnsureEngineConnected(AVAudioFormat format)
    {
        lock (engineLock)
        {
            if (engine == null)
            {
                engine = new AVAudioEngine();
                playerNode = new AVAudioPlayerNode();
                engine.AttachNode(playerNode);
            }

            if (connectedFormat is null
                || connectedFormat.SampleRate != format.SampleRate
                || connectedFormat.ChannelCount != format.ChannelCount)
            {
                try { playerNode!.RemoveTapOnBus(0); } catch { /* not installed yet */ }
                try { engine!.DisconnectNodeOutput(playerNode!); } catch { /* not connected yet */ }

                engine!.Connect(playerNode!, engine.MainMixerNode, format);
                playerNode!.InstallTapOnBus(0, 1024, format, (tapBuffer, _) =>
                {
                    var level = ComputeRms(tapBuffer);
                    AudioLevelChanged?.Invoke(this, level);
                });
                connectedFormat = format;
            }

            if (!engine!.Running)
            {
                engine.StartAndReturnError(out var error);
                if (error != null)
                    logger.LogWarning("AVAudioEngine failed to start: {Error}", error.LocalizedDescription);
            }
        }
    }

    static unsafe double ComputeRms(AVAudioPcmBuffer pcm)
    {
        var frames = (int)pcm.FrameLength;
        if (frames == 0)
            return 0;

        var channelDataPtr = pcm.FloatChannelData;
        if (channelDataPtr == IntPtr.Zero)
            return 0;

        var channels = (int)pcm.Format.ChannelCount;
        if (channels <= 0)
            return 0;

        var channelArray = (float**)channelDataPtr.ToPointer();
        double sumSquares = 0;
        long total = 0;
        for (var c = 0; c < channels; c++)
        {
            var samples = channelArray[c];
            for (var i = 0; i < frames; i++)
            {
                var s = samples[i];
                sumSquares += s * s;
            }
            total += frames;
        }

        if (total == 0)
            return 0;
        var rms = Math.Sqrt(sumSquares / total);
        return Math.Clamp(rms, 0.0, 1.0);
    }

    public Task StopAsync()
    {
        if (synthesizer.Speaking)
        {
            synthesizer.StopSpeaking(AVSpeechBoundary.Immediate);
            logger.LogDebug("Text-to-speech stopped");
        }

        try { playerNode?.Stop(); } catch { /* ignore */ }
        writeTcs?.TrySetResult();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { playerNode?.Stop(); } catch { /* ignore */ }
        try { engine?.Stop(); } catch { /* ignore */ }
        playerNode?.Dispose();
        engine?.Dispose();
        synthesizer.Dispose();
    }
}
