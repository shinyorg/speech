using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

/// <summary>
/// Platform-agnostic recorder built on <see cref="IAudioSource"/>. Every backend already normalizes
/// capture to 16 kHz mono PCM16, so there is nothing platform-specific left to do here.
/// </summary>
/// <remarks>
/// Capture is always started <b>dry</b> and the effect chain is applied in this class's own drain
/// loop. That is what makes <see cref="AudioRecordMode.Both"/> possible without teeing the capture
/// stream — <see cref="PipeStream"/> is single-reader, so a split would otherwise have to be built.
/// It also means the level reported here is measured on whichever signal is actually being written.
/// </remarks>
public class AudioRecorder(IAudioSource source, ILogger<AudioRecorder> logger) : IAudioRecorder
{
    const int SampleRate = 16000;
    const int Channels = 1;

    readonly Lock gate = new();

    CancellationTokenSource? cts;
    Task? drainTask;
    string? wetPath;
    string? dryPath;
    long bytesWritten;

    public event EventHandler<double>? InputLevelChanged;

    public bool IsRecording { get; private set; }

    public TimeSpan Elapsed => TimeSpan.FromSeconds(
        (double)Interlocked.Read(ref this.bytesWritten) / (SampleRate * Channels * 2)
    );

    public Task<AccessState> RequestAccess() => source.RequestAccess();

    public async Task StartAsync(AudioRecordingOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (this.gate)
        {
            if (this.IsRecording)
                throw new InvalidOperationException("A recording is already in progress. Call StopAsync() first.");

            this.IsRecording = true;
        }

        options ??= new AudioRecordingOptions();

        try
        {
            this.wetPath = ResolvePath(options.Path, options.Mode == AudioRecordMode.Dry ? "dry" : null);
            this.dryPath = options.Mode == AudioRecordMode.Both ? DerivePath(this.wetPath, "dry") : null;

            Directory.CreateDirectory(Path.GetDirectoryName(this.wetPath)!);
            Interlocked.Exchange(ref this.bytesWritten, 0);

            this.cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (options.MaxDuration is { } max)
                this.cts.CancelAfter(max);

            // Dry capture: the chain runs in the drain loop below, not in the source.
            var stream = await source
                .StartCaptureAsync(new AudioCaptureOptions { Processing = options.Processing }, this.cts.Token)
                .ConfigureAwait(false);

            this.drainTask = Task.Run(() => this.DrainAsync(stream, options, this.cts.Token), CancellationToken.None);

            logger.LogDebug("Audio recording started ({Mode}) → {Path}", options.Mode, this.wetPath);
        }
        catch
        {
            lock (this.gate)
                this.IsRecording = false;

            this.cts?.Dispose();
            this.cts = null;
            throw;
        }
    }

    async Task DrainAsync(Stream stream, AudioRecordingOptions options, CancellationToken token)
    {
        var effects = options.Mode == AudioRecordMode.Dry ? null : options.Effects;
        effects?.Reset();

        var throttle = new AudioLevelThrottle();
        var buffer = new byte[4096];

        // Written before the chain runs, so it holds the untouched microphone signal.
        byte[]? dryCopy = options.Mode == AudioRecordMode.Both ? new byte[buffer.Length] : null;

        await using var wet = new WavWriter(File.Create(this.wetPath!), SampleRate, Channels);
        await using var dry = this.dryPath == null
            ? null
            : new WavWriter(File.Create(this.dryPath), SampleRate, Channels);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0)
                    break;

                if (dryCopy != null)
                {
                    buffer.AsSpan(0, read).CopyTo(dryCopy);
                    dry!.Write(dryCopy.AsSpan(0, read));
                }

                if (options.Mode == AudioRecordMode.Dry)
                {
                    wet.Write(buffer.AsSpan(0, read));
                }
                else
                {
                    effects?.Process(MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, read)), SampleRate);
                    wet.Write(buffer.AsSpan(0, read));
                }

                Interlocked.Add(ref this.bytesWritten, read);

                if (throttle.TryEmit(AudioLevel.FromPcm16(buffer.AsSpan(0, read)), out var level))
                    this.InputLevelChanged?.Invoke(this, level);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The capture stream was torn down by StopAsync — expected.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audio recording drain failed");
        }
    }

    public async Task<AudioRecording?> StopAsync()
    {
        lock (this.gate)
        {
            if (!this.IsRecording)
                return null;

            this.IsRecording = false;
        }

        if (this.cts != null)
            await this.cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await source.StopCaptureAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to stop capture cleanly");
        }

        // Await the drain before reading the file: the WavWriter patches its size fields on
        // disposal, which happens when the loop exits.
        if (this.drainTask != null)
        {
            try
            {
                await this.drainTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Recording drain ended with an error");
            }
            this.drainTask = null;
        }

        this.cts?.Dispose();
        this.cts = null;

        var path = this.wetPath;
        var dry = this.dryPath;
        this.wetPath = null;
        this.dryPath = null;

        if (path == null || !File.Exists(path))
            return null;

        var size = new FileInfo(path).Length;
        if (size <= WavWriter.HeaderSize)
        {
            // Nothing but a header — treat an empty take as no recording rather than handing back
            // a file that will not play.
            TryDelete(path);
            TryDelete(dry);
            logger.LogDebug("Audio recording produced no samples");
            return null;
        }

        var duration = TimeSpan.FromSeconds((double)(size - WavWriter.HeaderSize) / (SampleRate * Channels * 2));
        logger.LogDebug("Audio recording stopped ({Duration}) → {Path}", duration, path);

        return new AudioRecording(path, dry, duration, SampleRate, Channels, size);
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Default location for recordings. There is no MAUI Essentials reference here, so
    /// <c>FileSystem.AppDataDirectory</c> is unavailable; <see cref="Environment.SpecialFolder.LocalApplicationData"/>
    /// resolves correctly on every target and matches what the Whisper model resolver already does.
    /// </summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "shiny.audio",
        "recordings"
    );

    static string ResolvePath(string? requested, string? suffix)
    {
        if (!String.IsNullOrWhiteSpace(requested))
            return Path.GetFullPath(requested);

        // Sortable, filename-safe, and unique enough for back-to-back takes.
        var name = $"recording-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
        if (suffix != null)
            name += $"-{suffix}";

        return Path.Combine(DefaultDirectory, name + ".wav");
    }

    static string DerivePath(string path, string suffix)
    {
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Path.Combine(directory, $"{name}-{suffix}{extension}");
    }

    static void TryDelete(string? path)
    {
        if (path == null)
            return;

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort — a leftover empty file is not worth failing the stop for.
        }
    }
}
