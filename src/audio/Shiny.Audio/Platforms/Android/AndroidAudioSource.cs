using Android;
using Android.Content.PM;
using Android.Media;
using Android.Media.Audiofx;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

public class AndroidAudioSource(AndroidPlatform platform, ILogger<AndroidAudioSource> logger) : IAudioSource
{
    AudioRecord? audioRecord;
    CancellationTokenSource? recordingCts;
    CaptureSink? sink;
    AcousticEchoCanceler? echoCanceler;
    NoiseSuppressor? noiseSuppressor;
    AutomaticGainControl? gainControl;

    public event EventHandler<double>? InputLevelChanged;

    public Task<AccessState> RequestAccess()
        => platform.RequestAccess(Manifest.Permission.RecordAudio);

    public Task<System.IO.Stream> StartCaptureAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var processing = options.Processing;

        const int sampleRate = 16000;
        const ChannelIn channelConfig = ChannelIn.Mono;
        const Encoding audioFormat = Encoding.Pcm16bit;

        if (Android.App.Application.Context.CheckSelfPermission(Manifest.Permission.RecordAudio) != Permission.Granted)
            throw new InvalidOperationException("RECORD_AUDIO permission has not been granted. Call RequestAccess() before starting capture.");

        var bufferSize = AudioRecord.GetMinBufferSize(sampleRate, channelConfig, audioFormat);
        if (bufferSize <= 0)
            bufferSize = 4096;

        // Echo cancellation needs a playback reference, which the voice-communication capture
        // pipeline supplies; routing through it also engages the platform's own AEC/NS/AGC on
        // most devices. Fall back to the raw mic when no processing is requested.
        var captureSource = processing?.EchoCancellation == true
            ? AudioSource.VoiceCommunication
            : AudioSource.Mic;

        audioRecord = new AudioRecord(
            captureSource,
            sampleRate,
            channelConfig,
            audioFormat,
            bufferSize
        );

        if (audioRecord.State != State.Initialized)
            throw new InvalidOperationException($"Failed to initialize AudioRecord (state={audioRecord.State}). The mic may be in use by another process.");

        if (processing?.AnyEnabled == true)
            this.AttachAudioEffects(audioRecord.AudioSessionId, processing);

        sink = new CaptureSink(options, level => InputLevelChanged?.Invoke(this, level), sampleRate);
        recordingCts = new CancellationTokenSource();
        audioRecord.StartRecording();

        // Capture locals — StopCaptureAsync nulls the fields concurrently with this
        // loop, so reading them per-iteration would NRE on the last buffer in flight.
        var token = recordingCts.Token;
        var record = audioRecord;
        var target = sink;

        _ = Task.Run(() =>
        {
            var buffer = new byte[bufferSize];
            while (!token.IsCancellationRequested)
            {
                var bytesRead = record.Read(buffer, 0, buffer.Length);
                if (bytesRead > 0 && !target.Write(buffer, 0, bytesRead))
                    break;   // consumer went away
            }
        }, token);

        logger.LogDebug("Android audio capture started");
        return Task.FromResult<System.IO.Stream>(sink.Stream);
    }

    // Each effect attaches to the AudioRecord's audio session and is best-effort: a device
    // whose hardware/driver lacks the effect reports IsAvailable == false and is skipped.
    void AttachAudioEffects(int audioSessionId, AudioProcessingOptions processing)
    {
        if (processing.EchoCancellation && AcousticEchoCanceler.IsAvailable)
        {
            echoCanceler = AcousticEchoCanceler.Create(audioSessionId);
            echoCanceler?.SetEnabled(true);
        }
        if (processing.NoiseSuppression && NoiseSuppressor.IsAvailable)
        {
            noiseSuppressor = NoiseSuppressor.Create(audioSessionId);
            noiseSuppressor?.SetEnabled(true);
        }
        if (processing.AutomaticGainControl && AutomaticGainControl.IsAvailable)
        {
            gainControl = AutomaticGainControl.Create(audioSessionId);
            gainControl?.SetEnabled(true);
        }

        logger.LogDebug(
            "Android audio effects — AEC:{Aec} NS:{Ns} AGC:{Agc}",
            echoCanceler != null, noiseSuppressor != null, gainControl != null
        );
    }

    void ReleaseAudioEffects()
    {
        echoCanceler?.Release();
        echoCanceler?.Dispose();
        echoCanceler = null;

        noiseSuppressor?.Release();
        noiseSuppressor?.Dispose();
        noiseSuppressor = null;

        gainControl?.Release();
        gainControl?.Dispose();
        gainControl = null;
    }

    public Task StopCaptureAsync()
    {
        recordingCts?.Cancel();
        recordingCts?.Dispose();
        recordingCts = null;

        ReleaseAudioEffects();

        if (audioRecord != null)
        {
            if (audioRecord.RecordingState == RecordState.Recording)
                audioRecord.Stop();

            audioRecord.Release();
            audioRecord = null;
        }

        sink?.Dispose();
        sink = null;

        logger.LogDebug("Android audio capture stopped");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        GC.SuppressFinalize(this);
    }
}
