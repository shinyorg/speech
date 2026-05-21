using Android.Media;
using Microsoft.Extensions.Logging;
using Stream = System.IO.Stream;

namespace Shiny.Speech;

public class AndroidAudioPlayer(ILogger<AndroidAudioPlayer> logger) : IAudioPlayer
{
    MediaPlayer? mediaPlayer;
    TaskCompletionSource? playbackTcs;

    public bool IsPlaying => mediaPlayer?.IsPlaying ?? false;

    public async Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        await StopAsync();

        var header = new byte[16];
        var headerRead = 0;
        while (headerRead < header.Length)
        {
            var n = await audioStream.ReadAsync(header.AsMemory(headerRead, header.Length - headerRead), cancellationToken);
            if (n == 0)
                break;
            headerRead += n;
        }

        var extension = SniffExtension(header, headerRead);
        var tempFile = Path.Combine(
            Android.App.Application.Context.CacheDir!.AbsolutePath,
            $"tts_{Guid.NewGuid()}{extension}"
        );

        try
        {
            await using (var fs = File.Create(tempFile))
            {
                if (headerRead > 0)
                    await fs.WriteAsync(header.AsMemory(0, headerRead), cancellationToken);
                await audioStream.CopyToAsync(fs, cancellationToken);
            }

            mediaPlayer = new MediaPlayer();
            playbackTcs = new TaskCompletionSource();

            mediaPlayer.Completion += OnCompletion;
            mediaPlayer.Error += OnError;

            await mediaPlayer.SetDataSourceAsync(tempFile);
            mediaPlayer.Prepare();

            using var reg = cancellationToken.Register(() =>
            {
                mediaPlayer?.Stop();
                playbackTcs?.TrySetResult();
            });

            mediaPlayer.Start();
            logger.LogDebug("Android audio playback started");

            await playbackTcs.Task;
            logger.LogDebug("Android audio playback finished");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    static string SniffExtension(byte[] header, int length)
    {
        if (length >= 12 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
            return ".m4a";

        if (length >= 4 && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F')
            return ".wav";

        if (length >= 4 && header[0] == (byte)'O' && header[1] == (byte)'g' && header[2] == (byte)'g' && header[3] == (byte)'S')
            return ".ogg";

        if (length >= 4 && header[0] == (byte)'f' && header[1] == (byte)'L' && header[2] == (byte)'a' && header[3] == (byte)'C')
            return ".flac";

        return ".mp3";
    }

    void OnCompletion(object? sender, EventArgs e)
        => playbackTcs?.TrySetResult();

    void OnError(object? sender, MediaPlayer.ErrorEventArgs e)
    {
        logger.LogWarning("Android audio playback error: {What} {Extra}", e.What, e.Extra);
        playbackTcs?.TrySetException(new InvalidOperationException($"Audio playback error: {e.What}"));
    }

    public Task StopAsync()
    {
        if (mediaPlayer != null)
        {
            if (mediaPlayer.IsPlaying)
                mediaPlayer.Stop();

            mediaPlayer.Release();
            mediaPlayer.Dispose();
            mediaPlayer = null;
            playbackTcs?.TrySetResult();
            logger.LogDebug("Android audio playback stopped");
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        mediaPlayer?.Release();
        mediaPlayer?.Dispose();
        mediaPlayer = null;
        return ValueTask.CompletedTask;
    }
}
