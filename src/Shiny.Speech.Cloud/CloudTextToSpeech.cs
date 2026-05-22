using System.Globalization;
using Microsoft.Extensions.Logging;
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
    public bool IsSpeaking => audioPlayer.IsPlaying;
    public bool IsPlayerAnalysisSupported => audioPlayer.IsPlayerAnalysisSupported;
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
        await audioPlayer.PlayAsync(audioStream, cancellationToken);

        logger.LogDebug("Cloud text-to-speech completed");
    }

    public Task StopAsync() => audioPlayer.StopAsync();
}
