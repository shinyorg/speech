namespace Shiny.Audio;

/// <summary>
/// One-stop entry point to the audio stack. Inject this to discover the whole surface; each member
/// is the same focused service you can also inject directly (they stay independently resolvable for
/// consumers that only need one).
/// </summary>
public interface IAudio
{
    /// <summary>Play a stream / file / URL.</summary>
    IAudioPlayer Player { get; }

    /// <summary>
    /// Capture microphone PCM as a pull stream. Transient — each access yields a fresh source, so
    /// resolve it once per capture session.
    /// </summary>
    IAudioSource Source { get; }

    /// <summary>Live mic-to-output monitoring (PA). iOS/Mac Catalyst and Android only.</summary>
    IAudioMonitor Monitor { get; }

    /// <summary>Audio input/output route enumeration and selection. iOS/Mac Catalyst and Android only.</summary>
    IAudioDevices Devices { get; }
}
