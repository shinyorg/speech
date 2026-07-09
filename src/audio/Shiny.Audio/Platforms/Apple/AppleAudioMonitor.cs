using System.Linq;
using System.Runtime.InteropServices;
using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

public class AppleAudioMonitor(ILogger<AppleAudioMonitor> logger) : IAudioMonitor
{
    AVAudioEngine? engine;
    NSObject? configObserver;
    bool rebuilding;
    double gain = 1.0;
    AudioDevice? outputPref;   // explicit output selection, if any (else auto: Bluetooth/wired > speaker)

#if !MACOS
    // Snapshot of the shared session so Stop restores it (see the same pattern in AppleAudioSource).
    string? priorCategory;
    AVAudioSessionCategoryOptions priorOptions;
    string? priorMode;
#endif

    public bool IsMonitoring { get; private set; }

    public event EventHandler<double>? InputLevelChanged;

    public double Gain
    {
        get => gain;
        set
        {
            gain = Math.Clamp(value, 0, 1);
            if (engine != null)
                engine.MainMixerNode.OutputVolume = (float)gain;
        }
    }

    public Task<AccessState> RequestAccess()
    {
#if MACOS
        return Task.FromResult(AccessState.Available);
#else
        var tcs = new TaskCompletionSource<AccessState>();
        AVAudioSession.SharedInstance().RequestRecordPermission(granted =>
            tcs.TrySetResult(granted ? AccessState.Available : AccessState.Denied));
        return tcs.Task;
#endif
    }

    public Task Start(AudioMonitorOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (IsMonitoring)
            return Task.CompletedTask;

        options ??= new AudioMonitorOptions();
        gain = Math.Clamp(options.Gain, 0, 1);
        engine = new AVAudioEngine();

#if !MACOS
        var session = AVAudioSession.SharedInstance();
        priorCategory = session.Category;
        priorOptions = session.CategoryOptions;
        priorMode = session.Mode;

        // No DefaultToSpeaker — it pins output to the built-in speaker and overrides a Bluetooth A2DP
        // route. AllowBluetoothA2DP lets output go to a Bluetooth *speaker* while the phone mic captures.
        session.SetCategory(
            AVAudioSessionCategory.PlayAndRecord,
            AVAudioSessionCategoryOptions.AllowBluetooth
                | AVAudioSessionCategoryOptions.AllowBluetoothA2DP,
            // NOTE: no AllowAirPlay — iOS won't carry a PlayAndRecord (live-mic) session over AirPlay.
            out var catErr);
        if (catErr != null)
            throw new InvalidOperationException($"Failed to set audio session category: {catErr.LocalizedDescription}");

        // VoiceChat mode + voice processing give AEC (feedback defense) but force Bluetooth onto the
        // low-quality HFP profile — which an A2DP-only speaker can't provide, so audio falls back to the
        // phone. Use it only when the caller asked for processing; otherwise Default mode, which lets
        // output route to a Bluetooth A2DP speaker.
        var wantsProcessing = options.Processing?.AnyEnabled == true;
        var mode = wantsProcessing ? AVAudioSessionMode.VoiceChat : AVAudioSessionMode.Default;
        session.SetMode(mode.GetConstant()!, out _);

        if (options.InputDevice != null)
            TrySetPreferredInput(session, options.InputDevice);

        session.SetActive(true, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var actErr);
        if (actErr != null)
            throw new InvalidOperationException($"Failed to activate audio session: {actErr.LocalizedDescription}");

        outputPref = options.OutputDevice;
        ApplyPreferredOutput(session);
#endif

        var input = engine.InputNode;

        if (wantsProcessing)
        {
            if (!input.SetVoiceProcessingEnabled(true, out var vpErr))
                logger.LogWarning("Failed to enable voice processing (AEC/NS/AGC): {Error}", vpErr?.LocalizedDescription);
        }

        ConnectAndStart(engine);

        // A route change (e.g. selecting a Bluetooth speaker) stops the engine and invalidates the
        // graph; rebuild and restart so monitoring follows the new route instead of going dead.
        configObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            AVAudioEngine.ConfigurationChangeNotification, _ => OnConfigChange(), engine);

        IsMonitoring = true;
        logger.LogDebug("Apple audio monitor started");
        return Task.CompletedTask;
    }

    // Wire input → main mixer → output, tap the input for VU, and (re)start the engine.
    void ConnectAndStart(AVAudioEngine e)
    {
        var input = e.InputNode;
        try { input.RemoveTapOnBus(0); } catch { /* no tap yet */ }

        var format = input.GetBusOutputFormat(0);
        e.Connect(input, e.MainMixerNode, format);
        e.MainMixerNode.OutputVolume = (float)gain;
        input.InstallTapOnBus(0, 1024, format, (buffer, when) => InputLevelChanged?.Invoke(this, ComputeLevel(buffer)));

        e.Prepare();
        e.StartAndReturnError(out var startErr);
        if (startErr != null)
            throw new InvalidOperationException($"Failed to start audio engine: {startErr.LocalizedDescription}");
    }

    void OnConfigChange()
    {
        if (!IsMonitoring || rebuilding || engine == null)
            return;

        rebuilding = true;
        try
        {
            ConnectAndStart(engine);   // engine has stopped on the route/format change — bring it back
#if !MACOS
            ApplyPreferredOutput(AVAudioSession.SharedInstance());   // re-route to a newly (dis)connected output
#endif
            logger.LogDebug("Apple audio monitor rebuilt after route change");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to rebuild audio monitor after route change");
        }
        finally
        {
            rebuilding = false;
        }
    }

    public Task SetInputDevice(AudioDevice? device)
    {
#if !MACOS
        var session = AVAudioSession.SharedInstance();
        if (device == null)
            session.SetPreferredInput(null, out _);
        else
            TrySetPreferredInput(session, device);
#endif
        return Task.CompletedTask;
    }

    public Task SetOutputDevice(AudioDevice? device)
    {
#if !MACOS
        outputPref = device;
        ApplyPreferredOutput(AVAudioSession.SharedInstance());
#endif
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        if (configObserver != null)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(configObserver);
            configObserver = null;
        }

        if (engine != null)
        {
            if (engine.Running)
            {
                engine.Stop();
                engine.InputNode.RemoveTapOnBus(0);
            }
            engine.Dispose();
            engine = null;
        }

#if !MACOS
        var session = AVAudioSession.SharedInstance();
        session.OverrideOutputAudioPort(AVAudioSessionPortOverride.None, out _);
        session.SetPreferredInput(null, out _);
        if (priorCategory != null)
        {
            session.SetCategory(new NSString(priorCategory), priorOptions, out _);
            if (priorMode != null)
                session.SetMode(new NSString(priorMode), out _);
        }
        session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
#endif

        IsMonitoring = false;
        logger.LogDebug("Apple audio monitor stopped");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await Stop();
        GC.SuppressFinalize(this);
    }

#if !MACOS
    void TrySetPreferredInput(AVAudioSession session, AudioDevice device)
    {
        var port = session.AvailableInputs?.FirstOrDefault(p => p.UID == device.Id);
        if (port != null)
            session.SetPreferredInput(port, out _);
    }

    // Apply the current output preference. Explicit BuiltInSpeaker → force speaker; explicit anything
    // else → clear the override so it follows that route. With no explicit pick, auto-route: keep an
    // external output (Bluetooth/wired) if one is connected, otherwise force the loud built-in speaker
    // so we don't sit on the quiet earpiece.
    void ApplyPreferredOutput(AVAudioSession session)
    {
        if (outputPref != null)
        {
            var over = outputPref.Type == AudioDeviceType.BuiltInSpeaker
                ? AVAudioSessionPortOverride.Speaker
                : AVAudioSessionPortOverride.None;
            session.OverrideOutputAudioPort(over, out _);
            return;
        }

        var current = session.CurrentRoute?.Outputs?.FirstOrDefault()?.PortType;
        var isExternal = current != null
            && current != AVAudioSession.PortBuiltInReceiver
            && current != AVAudioSession.PortBuiltInSpeaker;

        session.OverrideOutputAudioPort(
            isExternal ? AVAudioSessionPortOverride.None : AVAudioSessionPortOverride.Speaker,
            out _);
    }
#endif

    static double ComputeLevel(AVAudioPcmBuffer buffer)
    {
        var frames = (int)buffer.FrameLength;
        if (frames == 0 || buffer.FloatChannelData == IntPtr.Zero)
            return 0;

        var channel = Marshal.ReadIntPtr(buffer.FloatChannelData); // first channel
        var data = new float[frames];
        Marshal.Copy(channel, data, 0, frames);

        double sum = 0;
        foreach (var s in data)
            sum += s * s;
        return AudioLevel.FromRms(Math.Sqrt(sum / frames));
    }
}
