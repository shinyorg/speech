using System.Globalization;
using Microsoft.Extensions.Logging;
using Shiny.Audio;
using Shiny.Speech;

namespace Shiny.Speech.Cloud;

/// <summary>
/// ITextToSpeechService implementation that delegates synthesis to a pluggable ITextToSpeechProvider
/// and plays back the resulting audio using a platform-specific IAudioPlayer.
/// </summary>
public class CloudTextToSpeech : ITextToSpeechService
{
    readonly ITextToSpeechProvider provider;
    readonly IAudioPlayer audioPlayer;
    readonly ILogger<CloudTextToSpeech> logger;

    // The player can run several clips at once, so hold onto the utterance we started: speaking and
    // stopping then apply to this service's audio only, and never to whatever else the app is playing.
    IAudioPlayback? utterance;

    public CloudTextToSpeech(
        ITextToSpeechProvider provider,
        IAudioPlayer audioPlayer,
        ILogger<CloudTextToSpeech> logger
    )
    {
        this.provider = provider;
        this.audioPlayer = audioPlayer;
        this.logger = logger;

        audioPlayer.AudioLevelChanged += OnPlayerAudioLevelChanged;
    }

    public bool IsSupported => true;
    public bool IsSpeaking => this.utterance?.IsPlaying ?? false;
    public bool IsPlayerAnalysisSupported => audioPlayer.IsPlayerAnalysisSupported;
    public bool CanSynthesizeToStream => true;
    public event EventHandler<double>? AudioLevelChanged;

    void OnPlayerAudioLevelChanged(object? sender, double level)
        => AudioLevelChanged?.Invoke(this, level);

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CultureInfo? culture = null, CancellationToken cancellationToken = default)
        => provider.GetVoicesAsync(culture, cancellationToken);

    public async Task SpeakAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        await StopAsync();

        logger.LogDebug("Synthesizing speech via cloud provider");
        var audioStream = await provider.SynthesizeAsync(text, options, cancellationToken);

        logger.LogDebug("Playing synthesized audio");
        var playback = await audioPlayer.StartAsync(audioStream, cancellationToken);
        this.utterance = playback;

        try
        {
            await playback.Completion;
        }
        finally
        {
            Interlocked.CompareExchange(ref this.utterance, null, playback);
            await playback.DisposeAsync();
        }

        logger.LogDebug("Cloud text-to-speech completed");
    }

    public Task<Stream> SynthesizeToStreamAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Synthesizing speech to stream via cloud provider");
        return provider.SynthesizeAsync(text, options, cancellationToken);
    }

    public Task StopAsync()
    {
        var playback = Interlocked.Exchange(ref this.utterance, null);
        return playback?.StopAsync() ?? Task.CompletedTask;
    }
}
