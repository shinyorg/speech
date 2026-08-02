using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace Shiny.Audio;

public class WindowsAudioSource(ILogger<WindowsAudioSource> logger) : IAudioSource
{
    AudioGraph? audioGraph;
    AudioDeviceInputNode? inputNode;
    AudioFrameOutputNode? outputNode;
    CaptureSink? sink;

    public event EventHandler<double>? InputLevelChanged;

    public async Task<Stream> StartCaptureAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var processing = options.Processing;

        var encoding = AudioEncodingProperties.CreatePcm(16000, 1, 16);

        // AudioGraph only exposes Raw vs Default capture processing (no per-effect control).
        // The Communications capture category requests the endpoint's voice pipeline, which
        // engages driver-provided AEC/NS when available; Speech is used for raw capture.
        var wantsProcessing = processing?.AnyEnabled == true;
        var captureCategory = wantsProcessing
            ? Windows.Media.Capture.MediaCategory.Communications
            : Windows.Media.Capture.MediaCategory.Speech;

        var settings = new AudioGraphSettings(AudioRenderCategory.Speech)
        {
            EncodingProperties = encoding,
            DesiredRenderDeviceAudioProcessing = AudioProcessing.Default
        };

        var graphResult = await AudioGraph.CreateAsync(settings);
        if (graphResult.Status != AudioGraphCreationStatus.Success)
            throw new InvalidOperationException($"Failed to create audio graph: {graphResult.Status}");

        audioGraph = graphResult.Graph;

        var inputResult = await audioGraph.CreateDeviceInputNodeAsync(captureCategory, encoding);
        if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
            throw new InvalidOperationException($"Failed to create input node: {inputResult.Status}");

        inputNode = inputResult.DeviceInputNode;
        outputNode = audioGraph.CreateFrameOutputNode(encoding);
        inputNode.AddOutgoingConnection(outputNode);

        sink = new CaptureSink(options, level => InputLevelChanged?.Invoke(this, level));

        audioGraph.QuantumStarted += (graph, _) =>
        {
            var frame = outputNode.GetFrame();
            ProcessAudioFrame(frame);
        };

        audioGraph.Start();
        logger.LogDebug("Windows audio capture started");
        return sink.Stream;
    }

    void ProcessAudioFrame(AudioFrame frame)
    {
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        unsafe
        {
            byte* dataPtr = null;
            uint capacity = 0;
            ((IMemoryBufferByteAccess)reference).GetBuffer(&dataPtr, &capacity);
            if (capacity > 0 && dataPtr != null)
            {
                var data = new byte[capacity];
                Marshal.Copy((IntPtr)dataPtr, data, 0, (int)capacity);

                // QuantumStarted fires every 10ms; the sink throttles metering so a meter doesn't
                // get 100 events/sec.
                sink?.Write(data, 0, data.Length);
            }
        }
    }

    public Task StopCaptureAsync()
    {
        audioGraph?.Stop();
        inputNode?.Dispose();
        outputNode?.Dispose();
        audioGraph?.Dispose();
        sink?.Dispose();

        inputNode = null;
        outputNode = null;
        audioGraph = null;
        sink = null;

        logger.LogDebug("Windows audio capture stopped");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        GC.SuppressFinalize(this);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(byte** buffer, uint* capacity);
    }
}
