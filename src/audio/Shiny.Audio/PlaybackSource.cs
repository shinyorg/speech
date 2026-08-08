namespace Shiny.Audio;

/// <summary>
/// Helpers for resolving a playback source string (remote URL or local file path) that
/// <see cref="IAudioPlayer"/> implementations share.
/// </summary>
static class PlaybackSource
{
    static readonly HttpClient http = new();

    /// <summary>
    /// True when <paramref name="source"/> is an absolute http/https URL; otherwise it is
    /// treated as a local file system path.
    /// </summary>
    internal static bool IsRemote(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Download a remote source into memory.</summary>
    internal static Task<byte[]> DownloadAsync(string source, CancellationToken cancellationToken) =>
        http.GetByteArrayAsync(source, cancellationToken);

    /// <summary>
    /// Default resolution used by the <see cref="IAudioPlayer.StartAsync(string, CancellationToken)"/>
    /// interface method: opens a local file or downloads a remote URL, then starts it as a stream.
    /// Platform implementations override <c>StartAsync(string, …)</c> to hand the source to the
    /// native player directly (e.g. progressive streaming of remote URLs).
    /// </summary>
    /// <remarks>
    /// The stream is closed as soon as <c>StartAsync</c> returns, so this is only usable by players
    /// that fully consume (buffer or decode) the stream before starting playback.
    /// </remarks>
    internal static async Task<IAudioPlayback> StartResolvedAsync(IAudioPlayer player, string source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source must be a non-empty URL or file path.", nameof(source));

        if (IsRemote(source))
        {
            var bytes = await DownloadAsync(source, cancellationToken).ConfigureAwait(false);
            using var ms = new MemoryStream(bytes);
            return await player.StartAsync(ms, cancellationToken).ConfigureAwait(false);
        }

        await using var fs = File.OpenRead(source);
        return await player.StartAsync(fs, cancellationToken).ConfigureAwait(false);
    }
}
