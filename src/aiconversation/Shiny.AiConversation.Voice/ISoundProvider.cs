using Shiny.Audio;

namespace Shiny.AiConversation.Voice;

public enum AiAction
{
    Ok,
    Cancel,
    Respond,
    Think,
    Error
}

/// <summary>
/// Plays the short cues at voice state changes. Playback failures are logged, never fatal.
/// </summary>
public interface ISoundProvider
{
    Task Play(AiAction action);
}


/// <summary>
/// Plays the built-in cues through <see cref="IAudioPlayer"/>; does nothing when no player is registered.
/// </summary>
public class DefaultSoundProvider(IAudioPlayer? audioPlayer = null) : ISoundProvider
{
    public async Task Play(AiAction action)
    {
        if (audioPlayer == null)
            return;

        var resourceName = action switch
        {
            AiAction.Ok => "ok.wav",
            AiAction.Think => "think.wav",
            AiAction.Respond => "responding.wav",
            AiAction.Cancel => "cancel.wav",
            AiAction.Error => "error.wav",
            _ => null
        };
        if (resourceName == null)
            return;

        var assembly = typeof(DefaultSoundProvider).Assembly;
        var fullName = assembly.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith(resourceName, StringComparison.Ordinal));
        if (fullName == null)
            return;

        await using var stream = assembly.GetManifestResourceStream(fullName)!;
        await audioPlayer.PlayAsync(stream).ConfigureAwait(false);
    }
}


/// <summary>
/// An on-device wake word spotter. When one is registered the voice conversation keeps speech-to-text closed
/// while waiting for the wake word and only opens it afterwards, so a cloud recognizer isn't streaming (and
/// billing for) all room audio. Without one, the wake word is matched by the speech-to-text engine itself.
/// </summary>
public interface IWakeWordDetector
{
    /// <summary>
    /// Yields each time one of <paramref name="wakeWords"/> is heard, until cancelled.
    /// </summary>
    IAsyncEnumerable<string> Listen(IReadOnlyList<string> wakeWords, CancellationToken ct);
}
