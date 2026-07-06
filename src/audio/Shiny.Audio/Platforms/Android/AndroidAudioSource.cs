using Android;
using Android.Content.PM;
using Android.Media;
using Android.Media.Audiofx;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

public class AndroidAudioSource(ActivityProvider activityProvider, ILogger<AndroidAudioSource> logger) : IAudioSource
{
    AudioRecord? audioRecord;
    CancellationTokenSource? recordingCts;
    PipeStream? pipe;
    AcousticEchoCanceler? echoCanceler;
    NoiseSuppressor? noiseSuppressor;
    AutomaticGainControl? gainControl;

    public async Task<AccessState> RequestAccess()
    {
        var context = Android.App.Application.Context;
        if (context.CheckSelfPermission(Manifest.Permission.RecordAudio) == Permission.Granted)
            return AccessState.Available;

        var activity = activityProvider.Current;
        if (activity is not AndroidX.Fragment.App.FragmentActivity fragmentActivity)
            throw new InvalidOperationException("Current activity must be a FragmentActivity to request permissions");

        var fragment = new PermissionRequestFragment();
        var granted = await fragment.RequestAsync(fragmentActivity, Manifest.Permission.RecordAudio);
        return granted ? AccessState.Available : AccessState.Denied;
    }

    public Task<System.IO.Stream> StartCaptureAsync(AudioProcessingOptions? processing = null, CancellationToken cancellationToken = default)
    {
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

        pipe = new PipeStream();
        recordingCts = new CancellationTokenSource();
        audioRecord.StartRecording();

        // Capture locals — StopCaptureAsync nulls the fields concurrently with this
        // loop, so reading them per-iteration would NRE on the last buffer in flight.
        var token = recordingCts.Token;
        var record = audioRecord;
        var sink = pipe;

        _ = Task.Run(() =>
        {
            var buffer = new byte[bufferSize];
            while (!token.IsCancellationRequested)
            {
                var bytesRead = record.Read(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    try
                    {
                        sink.Write(buffer, 0, bytesRead);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        // pipe writer was completed by Dispose on the Stop path
                        break;
                    }
                }
            }
        }, token);

        logger.LogDebug("Android audio capture started");
        return Task.FromResult<System.IO.Stream>(pipe);
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

        pipe?.Dispose();
        pipe = null;

        logger.LogDebug("Android audio capture stopped");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        GC.SuppressFinalize(this);
    }
}
