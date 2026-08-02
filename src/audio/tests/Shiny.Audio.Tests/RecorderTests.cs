using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Audio;

namespace Shiny.Audio.Tests;

/// <summary>
/// Stands in for a platform capture backend: pushes a canned PCM signal through a
/// <see cref="PipeStream"/> at the same shape a real source would.
/// </summary>
sealed class FakeAudioSource(short[] signal) : IAudioSource
{
    PipeStream? pipe;
    CancellationTokenSource? cts;
    Task? pump;

    public AudioCaptureOptions? LastOptions { get; private set; }

#pragma warning disable CS0067 // required by IAudioSource; the recorder meters the drained signal itself
    public event EventHandler<double>? InputLevelChanged;
#pragma warning restore CS0067

    public Task<AccessState> RequestAccess() => Task.FromResult(AccessState.Available);

    public Task<Stream> StartCaptureAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default)
    {
        this.LastOptions = options;
        this.pipe = new PipeStream();
        this.cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var sink = this.pipe;
        var token = this.cts.Token;

        this.pump = Task.Run(async () =>
        {
            var bytes = new byte[signal.Length * 2];
            Buffer.BlockCopy(signal, 0, bytes, 0, bytes.Length);

            const int chunk = 640;   // 20 ms, like a real backend
            for (var offset = 0; offset < bytes.Length && !token.IsCancellationRequested; offset += chunk)
            {
                var count = Math.Min(chunk, bytes.Length - offset);
                try
                {
                    sink.Write(bytes, offset, count);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                await Task.Yield();
            }
        }, CancellationToken.None);

        return Task.FromResult<Stream>(this.pipe);
    }

    public async Task StopCaptureAsync()
    {
        if (this.cts != null)
            await this.cts.CancelAsync();

        if (this.pump != null)
        {
            try { await this.pump; } catch { }
            this.pump = null;
        }

        this.pipe?.Dispose();
        this.pipe = null;
        this.cts?.Dispose();
        this.cts = null;
    }

    public async ValueTask DisposeAsync() => await this.StopCaptureAsync();
}

public class AudioRecorderTests
{
    static string TempPath(string name)
        => Path.Combine(Path.GetTempPath(), "shiny-audio-tests", Guid.NewGuid().ToString("n"), name);

    static AudioRecorder Create(short[] signal, out FakeAudioSource source)
    {
        source = new FakeAudioSource(signal);
        return new AudioRecorder(source, NullLogger<AudioRecorder>.Instance);
    }

    /// <summary>Record until the fake source has drained, then stop.</summary>
    static async Task<AudioRecording?> RecordAsync(AudioRecorder recorder, AudioRecordingOptions options, int expectedBytes)
    {
        await recorder.StartAsync(options);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (recorder.Elapsed.TotalSeconds * 32000 < expectedBytes - 640 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        return await recorder.StopAsync();
    }

    [Test]
    public async Task Records_APlayableWavFile()
    {
        var signal = SignalMath.Sine(440, 16000);
        var recorder = Create(signal, out _);
        var path = TempPath("take.wav");

        var result = await RecordAsync(recorder, new AudioRecordingOptions { Path = path }, signal.Length * 2);

        await Assert.That(result).IsNotNull();
        await Assert.That(File.Exists(result!.Path)).IsTrue();
        await Assert.That(result.SampleRate).IsEqualTo(16000);
        await Assert.That(result.Channels).IsEqualTo(1);
        await Assert.That(result.DryPath).IsNull();

        using var reader = WavReader.Open(result.Path);
        await Assert.That(reader.SampleRate).IsEqualTo(16000);
        await Assert.That(reader.DataLength).IsGreaterThan(0L);

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Test]
    public async Task CapturesDry_SoTheChainCanBeAppliedInTheDrainLoop()
    {
        // The recorder must not push effects into the source — that is what keeps Dry/Both possible.
        var recorder = Create(SignalMath.Sine(440, 3200), out var source);
        var path = TempPath("take.wav");

        await RecordAsync(
            recorder,
            new AudioRecordingOptions { Path = path, Effects = new AudioEffectChain(new GainEffect { Gain = 2f }) },
            6400
        );

        await Assert.That(source.LastOptions).IsNotNull();
        await Assert.That(source.LastOptions!.Effects).IsNull();

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Test]
    public async Task WetMode_WritesTheProcessedSignal()
    {
        var signal = SignalMath.Sine(440, 16000, amplitude: 4000);
        var recorder = Create(signal, out _);
        var path = TempPath("wet.wav");

        var chain = new AudioEffectChain(new GainEffect { Gain = 2f });
        var result = await RecordAsync(
            recorder,
            new AudioRecordingOptions { Path = path, Mode = AudioRecordMode.Wet, Effects = chain },
            signal.Length * 2
        );

        var recorded = ReadSamples(result!.Path);
        // Skip the gain smoothing ramp at the start.
        var ratio = SignalMath.Rms(recorded.AsSpan(2000)) / SignalMath.Rms(signal.AsSpan(2000, recorded.Length - 2000));

        await Assert.That(ratio).IsEqualTo(2.0).Within(0.1);

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Test]
    public async Task DryMode_IgnoresTheEffectChain()
    {
        var signal = SignalMath.Sine(440, 16000, amplitude: 4000);
        var recorder = Create(signal, out _);
        var path = TempPath("dry.wav");

        var result = await RecordAsync(
            recorder,
            new AudioRecordingOptions
            {
                Path = path,
                Mode = AudioRecordMode.Dry,
                Effects = new AudioEffectChain(new GainEffect { Gain = 8f })
            },
            signal.Length * 2
        );

        var recorded = ReadSamples(result!.Path);
        await Assert.That(recorded.AsSpan(0, recorded.Length).SequenceEqual(signal.AsSpan(0, recorded.Length))).IsTrue();

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Test]
    public async Task BothMode_WritesTwoFilesThatDiffer()
    {
        var signal = SignalMath.Sine(440, 16000, amplitude: 4000);
        var recorder = Create(signal, out _);
        var path = TempPath("take.wav");

        var result = await RecordAsync(
            recorder,
            new AudioRecordingOptions
            {
                Path = path,
                Mode = AudioRecordMode.Both,
                Effects = new AudioEffectChain(new GainEffect { Gain = 2f })
            },
            signal.Length * 2
        );

        await Assert.That(result!.DryPath).IsNotNull();
        await Assert.That(File.Exists(result.DryPath!)).IsTrue();

        var wet = ReadSamples(result.Path);
        var dry = ReadSamples(result.DryPath!);

        // Same take, different renderings: the dry file must match the source exactly and the wet
        // one must be the louder, processed version.
        await Assert.That(dry.AsSpan(0, dry.Length).SequenceEqual(signal.AsSpan(0, dry.Length))).IsTrue();
        await Assert.That(SignalMath.Rms(wet.AsSpan(2000))).IsGreaterThan(SignalMath.Rms(dry.AsSpan(2000)) * 1.5);

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Test]
    public async Task StartingTwice_Throws()
    {
        var recorder = Create(SignalMath.Sine(440, 32000), out _);
        var path = TempPath("take.wav");

        await recorder.StartAsync(new AudioRecordingOptions { Path = path });
        try
        {
            await Assert.That(async () => await recorder.StartAsync(new AudioRecordingOptions { Path = path }))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            await recorder.StopAsync();
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public async Task StoppingWithoutStarting_ReturnsNull()
    {
        var recorder = Create([], out _);
        await Assert.That(await recorder.StopAsync()).IsNull();
    }

    [Test]
    public async Task EmptyTake_ReturnsNullAndLeavesNoFile()
    {
        var recorder = Create([], out _);
        var path = TempPath("empty.wav");

        await recorder.StartAsync(new AudioRecordingOptions { Path = path });
        var result = await recorder.StopAsync();

        // A header-only WAV will not play; handing one back would be worse than saying "nothing".
        await Assert.That(result).IsNull();
        await Assert.That(File.Exists(path)).IsFalse();

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Test]
    public async Task IsRecording_TracksState()
    {
        var recorder = Create(SignalMath.Sine(440, 32000), out _);
        var path = TempPath("take.wav");

        await Assert.That(recorder.IsRecording).IsFalse();
        await recorder.StartAsync(new AudioRecordingOptions { Path = path });
        await Assert.That(recorder.IsRecording).IsTrue();
        await recorder.StopAsync();
        await Assert.That(recorder.IsRecording).IsFalse();

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Test]
    public async Task DefaultDirectory_IsUnderLocalAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "shiny.audio",
            "recordings"
        );
        await Assert.That(AudioRecorder.DefaultDirectory).IsEqualTo(expected);
    }

    static short[] ReadSamples(string path)
    {
        using var reader = WavReader.Open(path);
        var samples = new short[reader.DataLength / 2];
        var total = 0;
        int n;
        while ((n = reader.ReadSamples(samples.AsSpan(total))) > 0)
            total += n;

        return samples.AsSpan(0, total).ToArray();
    }
}

public class AudioEffectProcessorTests
{
    [Test]
    public async Task ProcessesAnExistingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "shiny-audio-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);

        var input = Path.Combine(directory, "in.wav");
        var output = Path.Combine(directory, "out.wav");

        var signal = SignalMath.Sine(440, 16000, amplitude: 4000);
        await using (var writer = new WavWriter(File.Create(input)))
            writer.Write(signal);

        var duration = AudioEffectProcessor.ProcessFile(
            input,
            output,
            new AudioEffectChain(new GainEffect { Gain = 2f })
        );

        await Assert.That(duration.TotalSeconds).IsEqualTo(1.0).Within(0.01);

        using var reader = WavReader.Open(output);
        var read = new short[signal.Length];
        var total = 0;
        int n;
        while ((n = reader.ReadSamples(read.AsSpan(total))) > 0)
            total += n;

        await Assert.That(total).IsEqualTo(signal.Length);
        await Assert.That(SignalMath.Rms(read.AsSpan(2000)) / SignalMath.Rms(signal.AsSpan(2000)))
            .IsEqualTo(2.0).Within(0.05);

        Directory.Delete(directory, true);
    }

    [Test]
    public async Task RenderingTwice_IsDeterministic()
    {
        // Re-rendering the same dry take with the same settings must give the same file, otherwise
        // "record dry, try settings" is not a usable workflow.
        var signal = SignalMath.Sine(440, 8000, amplitude: 6000);

        var first = signal.ToArray();
        AudioEffectProcessor.Process(first, new AudioEffectChain(new EchoEffect { DelayMs = 120f, Mix = 0.5f }));

        var second = signal.ToArray();
        AudioEffectProcessor.Process(second, new AudioEffectChain(new EchoEffect { DelayMs = 120f, Mix = 0.5f }));

        await Assert.That(first.SequenceEqual(second)).IsTrue();
    }

    [Test]
    public async Task InMemoryProcessing_AppliesTheChain()
    {
        var samples = SignalMath.Sine(440, 8000, amplitude: 4000);
        var original = samples.ToArray();

        AudioEffectProcessor.Process(samples, new AudioEffectChain(new GainEffect { Gain = 0.5f }));

        await Assert.That(SignalMath.Rms(samples.AsSpan(2000)) / SignalMath.Rms(original.AsSpan(2000)))
            .IsEqualTo(0.5).Within(0.02);
    }
}
