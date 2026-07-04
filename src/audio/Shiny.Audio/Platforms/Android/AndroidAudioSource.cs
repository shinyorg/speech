using Android;
using Android.Content.PM;
using Android.Media;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

public class AndroidAudioSource(ActivityProvider activityProvider, ILogger<AndroidAudioSource> logger) : IAudioSource
{
    AudioRecord? audioRecord;
    CancellationTokenSource? recordingCts;
    PipeStream? pipe;

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

    public Task<System.IO.Stream> StartCaptureAsync(CancellationToken cancellationToken = default)
    {
        const int sampleRate = 16000;
        const ChannelIn channelConfig = ChannelIn.Mono;
        const Encoding audioFormat = Encoding.Pcm16bit;

        if (Android.App.Application.Context.CheckSelfPermission(Manifest.Permission.RecordAudio) != Permission.Granted)
            throw new InvalidOperationException("RECORD_AUDIO permission has not been granted. Call RequestAccess() before starting capture.");

        var bufferSize = AudioRecord.GetMinBufferSize(sampleRate, channelConfig, audioFormat);
        if (bufferSize <= 0)
            bufferSize = 4096;

        audioRecord = new AudioRecord(
            AudioSource.Mic,
            sampleRate,
            channelConfig,
            audioFormat,
            bufferSize
        );

        if (audioRecord.State != State.Initialized)
            throw new InvalidOperationException($"Failed to initialize AudioRecord (state={audioRecord.State}). The mic may be in use by another process.");

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

    public Task StopCaptureAsync()
    {
        recordingCts?.Cancel();
        recordingCts?.Dispose();
        recordingCts = null;

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
