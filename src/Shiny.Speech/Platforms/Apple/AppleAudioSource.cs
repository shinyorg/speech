using AVFoundation;
using Microsoft.Extensions.Logging;

namespace Shiny.Speech;

public class AppleAudioSource(ILogger<AppleAudioSource> logger) : IAudioSource
{
    AVAudioEngine? audioEngine;
    Stream? outputStream;

    public Task<Stream> StartCaptureAsync(CancellationToken cancellationToken = default)
    {
        audioEngine = new AVAudioEngine();
        var inputNode = audioEngine.InputNode;

        var desiredFormat = new AVAudioFormat(
            AVAudioCommonFormat.PCMInt16,
            16000,
            1,
            false
        );

        var pipe = new PipeStream();
        outputStream = pipe;

        inputNode.InstallTapOnBus(0, 4096, inputNode.GetBusOutputFormat(0), (buffer, when) =>
        {
            var audioBuffer = buffer.AudioBufferList[0];
            if (audioBuffer.Data != IntPtr.Zero && audioBuffer.DataByteSize > 0)
            {
                var data = new byte[audioBuffer.DataByteSize];
                System.Runtime.InteropServices.Marshal.Copy(audioBuffer.Data, data, 0, data.Length);
                try
                {
                    pipe.Write(data, 0, data.Length);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        });

#if !MACOS
        var audioSession = AVAudioSession.SharedInstance();
        audioSession.SetCategory(AVAudioSessionCategory.Record, AVAudioSessionCategoryOptions.DefaultToSpeaker, out _);
        audioSession.SetActive(true, out _);
#endif

        audioEngine.Prepare();
        audioEngine.StartAndReturnError(out var error);
        if (error != null)
            throw new InvalidOperationException($"Failed to start audio engine: {error.LocalizedDescription}");

        logger.LogDebug("Apple audio capture started");
        return Task.FromResult<Stream>(pipe);
    }

    public Task StopCaptureAsync()
    {
        if (audioEngine != null)
        {
            if (audioEngine.Running)
            {
                audioEngine.Stop();
                audioEngine.InputNode.RemoveTapOnBus(0);
            }

#if !MACOS
            var session = AVAudioSession.SharedInstance();
            session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
#endif
        }

        outputStream?.Dispose();
        logger.LogDebug("Apple audio capture stopped");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        GC.SuppressFinalize(this);
    }
}
