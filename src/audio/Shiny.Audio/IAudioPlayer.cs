namespace Shiny.Audio;

/// <summary>
/// Platform-specific audio playback.
/// </summary>
public interface IAudioPlayer : IAsyncDisposable
{
    /// <summary>
    /// Play an audio stream (e.g. MP3). Completes when playback finishes or is cancelled.
    /// </summary>
    Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Play audio from a remote URL (<c>http</c>/<c>https</c>) or a local file path. The platform
    /// determines how to load the source — you never need to build a platform-specific file URI.
    /// Completes when playback finishes or is cancelled.
    /// </summary>
    /// <param name="source">An absolute <c>http</c>/<c>https</c> URL, or a local file system path.</param>
    Task PlayAsync(string source, CancellationToken cancellationToken = default)
        => PlaybackSource.PlayResolvedAsync(this, source, cancellationToken);

    /// <summary>
    /// Stop any current playback.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Whether audio is currently playing.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    /// True when the platform can emit <see cref="AudioLevelChanged"/> samples during playback.
    /// </summary>
    bool IsPlayerAnalysisSupported { get; }

    /// <summary>
    /// Fires periodically during playback with the current output level normalized to 0.0 - 1.0.
    /// Only fires on platforms where <see cref="IsPlayerAnalysisSupported"/> is true.
    /// </summary>
    event EventHandler<double>? AudioLevelChanged;

    /// <summary>
    /// Gets a value indicating whether <b>setting</b> <see cref="Volume"/> is supported on the current platform.
    /// </summary>
    /// <remarks>
    /// Reading <see cref="Volume"/> and observing <see cref="VolumeChanged"/> work on every platform that has an
    /// <see cref="IAudioPlayer"/>. Only <i>setting</i> is platform-limited:
    /// <list type="bullet">
    /// <item><description><b>Android</b>: <c>true</c> — writes the system <c>STREAM_MUSIC</c> level.</description></item>
    /// <item><description><b>Windows</b>: <c>true</c> — writes the default render endpoint's master volume.</description></item>
    /// <item><description><b>macOS</b>: <c>true</c> when the current default output device exposes a settable virtual
    /// main volume (most built-in and USB devices do; some HDMI/aggregate devices do not).</description></item>
    /// <item><description><b>Browser (WebAssembly)</b>: <c>true</c> — writes the app's own media-element volume
    /// (browsers sandbox the OS volume, so this is per-app, not device-wide).</description></item>
    /// <item><description><b>iOS / Mac Catalyst</b>: <c>false</c> — Apple exposes no supported API to change the
    /// system volume; the setter throws <see cref="NotSupportedException"/>.</description></item>
    /// </list>
    /// Always check this before assigning <see cref="Volume"/>.
    /// </remarks>
    bool IsVolumeControlSupported { get; }

    /// <summary>
    /// Gets or sets the playback volume, normalized 0.0–1.0.
    /// </summary>
    /// <remarks>
    /// On device platforms (Android, Apple, Windows) this is the <b>system media volume</b> — the same level the
    /// hardware buttons control — and is independent of any per-utterance volume set on a TTS request. In the
    /// browser it is the app's own media-element volume (browsers do not expose the OS volume).
    /// <para>
    /// Reading works wherever an <see cref="IAudioPlayer"/> exists. Setting is only supported where
    /// <see cref="IsVolumeControlSupported"/> is <c>true</c>; on iOS / Mac Catalyst the setter throws
    /// <see cref="NotSupportedException"/> — let the user adjust volume with the hardware buttons or an
    /// <c>MPVolumeView</c>. On Android the underlying stream is integer-stepped, so a value you set is quantized to
    /// the nearest step and reading it back may differ slightly.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Thrown by the setter on iOS / Mac Catalyst; guard with <see cref="IsVolumeControlSupported"/>.</exception>
    float Volume { get; set; }

    /// <summary>
    /// Raised when the volume changes — via the hardware buttons, the OS volume UI, or a successful
    /// <see cref="Volume"/> set. The argument is the new volume normalized 0.0–1.0.
    /// </summary>
    event EventHandler<float>? VolumeChanged;
}
