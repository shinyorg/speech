namespace Shiny.Audio;

/// <summary>
/// Live microphone monitoring: routes captured mic input straight to the current audio output in
/// near-real-time (a "PA"/megaphone). Unlike <see cref="IAudioSource"/> (which hands you a PCM
/// stream to consume), this wires input → output on the platform's own audio graph.
/// </summary>
/// <remarks>
/// Feedback (the mic hearing the speaker) is the main hazard. Keep the phone away from the speaker,
/// enable <see cref="AudioProcessingOptions.EchoCancellation"/>, and/or lower <see cref="Gain"/>.
/// </remarks>
public interface IAudioMonitor : IAsyncDisposable
{
    /// <summary>Request the runtime microphone permission required to monitor.</summary>
    Task<AccessState> RequestAccess();

    /// <summary>Start routing the mic to the output.</summary>
    Task Start(AudioMonitorOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Stop monitoring and restore the audio session.</summary>
    Task Stop();

    /// <summary>True while monitoring is active.</summary>
    bool IsMonitoring { get; }

    /// <summary>Output level of the monitored signal, 0.0 (mute) – 1.0 (unity). Adjustable live.</summary>
    double Gain { get; set; }

    /// <summary>
    /// Choose the capture device (from <see cref="IAudioDevices.GetInputs"/>). Pass null to let the
    /// OS pick its default. Applies live when monitoring.
    /// </summary>
    Task SetInputDevice(AudioDevice? device);

    /// <summary>
    /// Choose the render device (from <see cref="IAudioDevices.GetOutputs"/>). Pass null for the OS
    /// default. Best-effort: on iOS only the built-in speaker/receiver override is programmatic —
    /// route Bluetooth/AirPlay via <see cref="IAudioDevices.ShowOutputPicker"/>.
    /// </summary>
    Task SetOutputDevice(AudioDevice? device);

    /// <summary>Fires periodically while monitoring with the input level normalized 0.0 – 1.0.</summary>
    event EventHandler<double>? InputLevelChanged;
}

/// <summary>Options for <see cref="IAudioMonitor.Start"/>.</summary>
public record AudioMonitorOptions
{
    /// <summary>Voice-processing effects (echo cancellation / noise suppression / AGC). Null = raw.</summary>
    public AudioProcessingOptions? Processing { get; init; }

    /// <summary>Initial output gain, 0.0 – 1.0. Default 1.0.</summary>
    public double Gain { get; init; } = 1.0;

    /// <summary>Preferred capture device. Null = OS default.</summary>
    public AudioDevice? InputDevice { get; init; }

    /// <summary>Preferred render device. Null = OS default.</summary>
    public AudioDevice? OutputDevice { get; init; }
}
