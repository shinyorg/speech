using System.Linq;
using System.Runtime.InteropServices;
using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using Shiny.Audio.Duplex;

namespace Shiny.Audio;

/// <summary>
/// <see cref="IAudioDuplex"/> on iOS, Mac Catalyst and macOS: one <see cref="AVAudioEngine"/> whose input
/// node runs Apple's voice-processing I/O unit (AEC/NS/AGC), so what its player node plays is cancelled
/// out of what its input tap captures.
/// </summary>
public class AppleAudioDuplex(ILogger<AppleAudioDuplex> logger) : IAudioDuplex
{
    readonly AudioDuplexSlot slot = new();

    public Task<AccessState> RequestAccess()
    {
#if MACOS
        // macOS gates the microphone too: without asking first, the engine's input unit refuses to initialize (-10875)
        return MacMicrophoneAccess.Request();
#else
        var tcs = new TaskCompletionSource<AccessState>(TaskCreationOptions.RunContinuationsAsynchronously);
        AVAudioSession.SharedInstance().RequestRecordPermission(granted =>
            tcs.TrySetResult(granted ? AccessState.Available : AccessState.Denied));
        return tcs.Task;
#endif
    }

    public Task<IAudioDuplexSession> StartAsync(AudioDuplexOptions? options = null, CancellationToken cancellationToken = default)
        => this.slot.StartAsync(options, cancellationToken, (opts, ct) =>
        {
            var session = new AppleAudioDuplexSession(opts, logger);
            try
            {
                session.Start();
            }
            catch
            {
                // Put the shared audio session back before reporting the failure.
                _ = session.StopAsync();
                throw;
            }
            return Task.FromResult<AudioDuplexSession>(session);
        });
}


sealed class AppleAudioDuplexSession(AudioDuplexOptions options, ILogger logger) : AudioDuplexSession(options)
{
    // Buffers kept scheduled on the player node. Each completion refills and reschedules its buffer, so
    // the node always has this many frames ahead of the hardware; the jitter buffer sits in front of it.
    const int ScheduledBuffers = 3;

    AVAudioEngine? engine;
    AVAudioPlayerNode? player;
    AVAudioFormat? playFormat;
    AVAudioFormat? captureFormat;
    AVAudioConverter? converter;
    NSObject? configObserver;
    readonly object graphGate = new();   // serializes engine rebuilds: route changes vs. a live input switch
    volatile bool running;
    volatile int generation;   // bumped on every (re)start, so completions from a flushed player don't reschedule
    bool rebuilding;
    bool speakerphone = options.Speakerphone;

#if !MACOS
    string? priorCategory;
    AVAudioSessionCategoryOptions priorOptions;
    string? priorMode;
    double priorIoBufferDuration;
    AVAudioSessionPortDescription? priorPreferredInput;
    NSObject? routeObserver;
#endif

    bool WantsProcessing => this.Options.Processing?.AnyEnabled == true && !this.processingFailed;

    // the voice-processing unit wouldn't start on this device (seen on macOS as -10875); the session carries on without it
    bool processingFailed;

    public override bool Speakerphone
    {
        get => this.speakerphone;
        set
        {
            if (this.speakerphone == value)
                return;

            this.speakerphone = value;
#if !MACOS
            if (this.running)
            {
                var session = AVAudioSession.SharedInstance();
                // DefaultToSpeaker is a category option, so turning it on/off means re-setting the
                // category (the same category, so the session stays active).
                session.SetCategory(AVAudioSessionCategory.PlayAndRecord, this.CategoryOptions(), out var err);
                if (err != null)
                    logger.LogWarning("Failed to update the audio session for speakerphone={Speaker}: {Error}", value, err.LocalizedDescription);
                this.ApplySpeakerphone(session);
            }
#endif
        }
    }

    public void Start()
    {
        var rate = this.Options.SampleRate;

#if !MACOS
        var session = AVAudioSession.SharedInstance();
        this.priorCategory = session.Category;
        this.priorOptions = session.CategoryOptions;
        this.priorMode = session.Mode;
        this.priorIoBufferDuration = session.IOBufferDuration;

        session.SetCategory(AVAudioSessionCategory.PlayAndRecord, this.CategoryOptions(), out var catErr);
        if (catErr != null)
            throw new InvalidOperationException($"Failed to set audio session category: {catErr.LocalizedDescription}");

        // VoiceChat is the telephony profile: it engages the session-level voice processing that matches
        // the engine's voice-processing unit below. Without processing, Default keeps the route as is.
        var mode = this.WantsProcessing ? AVAudioSessionMode.VoiceChat : AVAudioSessionMode.Default;
        session.SetMode(mode.GetConstant()!, out _);

        // Ask for an I/O cycle of one frame, so capture comes back in small buffers instead of ~100 ms
        // ones. A preference: the hardware may round it.
        session.SetPreferredIOBufferDuration(this.Options.FrameDuration.TotalSeconds, out _);

        session.SetActive(true, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var actErr);
        if (actErr != null)
            throw new InvalidOperationException($"Failed to activate audio session: {actErr.LocalizedDescription}");

        // iOS routes by preference rather than by device: the chosen mic becomes the preferred input and
        // the route follows. The app's own preference is put back on stop.
        this.priorPreferredInput = session.PreferredInput;
        var inputApplied = this.ApplyPreferredInput(session, this.InputDevice);

        this.ApplySpeakerphone(session, inputApplied ? this.InputDevice : null);

        // The chosen mic going away (unplugged, Bluetooth off) drops the session back to the default.
        this.routeObserver = AVAudioSession.Notifications.ObserveRouteChange((_, _) => this.OnRouteChange());
#endif

        this.captureFormat = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, rate, 1, false);
        this.playFormat = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, rate, 1, false);

        this.running = true;
#if MACOS
        var inputApplied = this.BuildEngine(this.InputDevice);
#else
        this.BuildEngine();
#endif
        if (!inputApplied)
            this.OnInputDeviceUnavailable(notify: false);

#if MACOS
        logger.LogDebug("Apple audio duplex started ({Rate}Hz, processing={Processing})", rate, this.WantsProcessing);
#else
        logger.LogDebug(
            "Apple audio duplex started ({Rate}Hz, processing={Processing}, input={Input}, output={Output})",
            rate,
            this.WantsProcessing,
            session.CurrentRoute?.Inputs?.FirstOrDefault()?.PortType ?? "unknown",
            session.CurrentRoute?.Outputs?.FirstOrDefault()?.PortType ?? "unknown"
        );
#endif
    }

    // A fresh engine and player node, wired and started. On macOS the input device is chosen here, between
    // enabling voice processing and reading the input format; returns false if that device is gone.
#if MACOS
    bool BuildEngine(AudioDevice? input)
#else
    void BuildEngine()
#endif
    {
        var e = new AVAudioEngine();
        this.engine = e;

        // Build the output side (main mixer -> output node) BEFORE enabling voice processing. On macOS, an output
        // graph created after voice processing is on fails to initialize the engine (-10875); created first, the
        // voice-processing unit takes it over cleanly. Harmless on iOS.
        _ = e.MainMixerNode;

        // Voice processing has to be enabled before the engine is prepared, and it swaps the I/O unit
        // under BOTH the input and output nodes: that's what gives the canceller its playback reference.
        if (this.WantsProcessing && !e.InputNode.SetVoiceProcessingEnabled(true, out var vpErr))
            logger.LogWarning("Failed to enable voice processing (AEC/NS/AGC): {Error}", vpErr?.LocalizedDescription);

#if MACOS
        // After voice processing, since enabling that swaps the unit underneath the node (the same order
        // AppleAudioSource uses).
        var applied = this.SelectMacInput(e.InputNode, input);
#endif

        var p = new AVAudioPlayerNode();
        this.player = p;
        e.AttachNode(p);

        try
        {
            this.ConnectAndStart(e);
        }
        catch (InvalidOperationException ex) when (this.WantsProcessing)
        {
            // A call without echo cancellation beats no call: rebuild with the plain I/O unit
            logger.LogWarning(ex, "The voice-processing audio unit failed to start - continuing without echo cancellation, noise suppression and AGC");
            this.TeardownEngine();
            this.processingFailed = true;
#if MACOS
            return this.BuildEngine(input);
#else
            this.BuildEngine();
            return;
#endif
        }

        // A route change (headset plugged in, Bluetooth connected, speaker toggled) stops the engine and
        // can change the input format. Rebuild so the call keeps going instead of going silent.
        this.configObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            AVAudioEngine.ConfigurationChangeNotification,
            _ => this.OnConfigurationChange(),
            e
        );
#if MACOS
        return applied;
#endif
    }

    void TeardownEngine()
    {
        if (this.configObserver != null)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(this.configObserver);
            this.configObserver = null;
        }

        // Old buffers' completions belong to the previous generation and end their chains.
        this.generation++;

        if (this.engine != null)
        {
            try
            {
                this.player?.Stop();
                if (this.engine.Running)
                    this.engine.Stop();
                this.engine.InputNode.RemoveTapOnBus(0);
                if (this.WantsProcessing)
                    this.engine.InputNode.SetVoiceProcessingEnabled(false, out _);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error tearing down the duplex audio engine");
            }
            this.engine.Dispose();
            this.engine = null;
        }

        this.player?.Dispose();
        this.player = null;
        this.converter?.Dispose();
        this.converter = null;
    }

    protected override bool SelectInputDevice(AudioDevice? device)
    {
#if MACOS
        // The voice-processing unit is bound to its device when the engine is prepared, so a live switch
        // is a rebuild: a fresh engine on the new mic. Capture and playback pause for that restart only;
        // the jitter buffer keeps what's queued.
        bool applied;
        lock (this.graphGate)
        {
            if (!this.running)
                return true;

            this.rebuilding = true;
            try
            {
                this.TeardownEngine();
                try
                {
                    applied = this.BuildEngine(device);
                }
                catch (Exception ex) when (device != null)
                {
                    logger.LogWarning(ex, "Failed to start the duplex on input {Device}; using the system default", device.Name);
                    this.TeardownEngine();
                    this.BuildEngine(null);
                    applied = false;
                }
            }
            finally
            {
                this.rebuilding = false;
            }
        }
        logger.LogDebug("Apple audio duplex switched input to {Device}", applied ? device?.Name ?? "the system default" : "the system default");
        return applied;
#else
        if (!this.running)
            return true;

        // The route change that follows fires the engine's configuration change, which rebuilds the graph
        // on the new input (and re-applies the speaker override, which depends on the new route).
        var session = AVAudioSession.SharedInstance();
        var applied = this.ApplyPreferredInput(session, device);
        this.ApplySpeakerphone(session, applied ? device : null);
        return applied;
#endif
    }

    // (Re)wire the graph: player → main mixer (which resamples to the hardware), tap on the input,
    // start the engine and the player, and keep the player's buffers scheduled.
    void ConnectAndStart(AVAudioEngine e)
    {
        var input = e.InputNode;
        try { input.RemoveTapOnBus(0); } catch { /* no tap yet */ }

        var p = this.player!;
        e.Connect(p, e.MainMixerNode, this.playFormat);

        // The input's format is only valid once the session is active and voice processing is applied.
        var inputFormat = input.GetBusOutputFormat(0);

        // macOS voice processing reports several channels (9 on a MacBook); the first is the processed voice.
        // Tap mono at the hardware rate, so the converter gets the voice rather than a downmix of every channel.
        if (inputFormat.ChannelCount > 1 && inputFormat.SampleRate > 0)
            inputFormat = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, inputFormat.SampleRate, 1, false);
        if (inputFormat.SampleRate <= 0 || inputFormat.ChannelCount == 0)
            throw new InvalidOperationException(
                $"Microphone input format is invalid (sampleRate={inputFormat.SampleRate}, channels={inputFormat.ChannelCount}). " +
                "The audio session may not be active, or no audio input is available on this device."
            );

        this.converter?.Dispose();
        var conv = new AVAudioConverter(inputFormat, this.captureFormat!);
        this.converter = conv;

        var tapFrames = (uint)Math.Max(256, inputFormat.SampleRate * this.Options.FrameDuration.TotalSeconds);
        input.InstallTapOnBus(0, tapFrames, inputFormat, (buffer, _) =>
        {
            if (!this.running)
                return;

            try
            {
                var data = this.Convert(conv, buffer);
                if (data.Length > 0)
                    this.OnCaptured(data);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to convert a captured duplex buffer");
            }
        });

        e.Prepare();
        e.StartAndReturnError(out var startErr);
        if (startErr != null)
            throw new InvalidOperationException($"Failed to start audio engine: {startErr.LocalizedDescription}");

        // Fresh buffers on every (re)start: a stopped player flushed whatever was scheduled, and the old
        // buffers' completions belong to the previous generation, so they end their chains.
        var gen = ++this.generation;
        var frames = (uint)(this.Options.FrameBytes / 2);
        for (var i = 0; i < ScheduledBuffers; i++)
            this.Schedule(new AVAudioPcmBuffer(this.playFormat!, frames), new byte[this.Options.FrameBytes], gen);

        p.Play();
    }

    // Refill one player buffer from the jitter buffer and schedule it; its completion does the same, so
    // the player is fed at exactly the rate the hardware consumes it.
    void Schedule(AVAudioPcmBuffer buffer, byte[] scratch, int gen)
    {
        if (!this.running || gen != this.generation || this.player is not { } p)
            return;

        this.ReadPlayback(scratch);

        var frames = scratch.Length / 2;
        unsafe
        {
            var channel = (float*)Marshal.ReadIntPtr(buffer.FloatChannelData);
            fixed (byte* src = scratch)
            {
                var pcm = (short*)src;
                for (var i = 0; i < frames; i++)
                    channel[i] = pcm[i] / 32768f;
            }
        }
        buffer.FrameLength = (uint)frames;

        try
        {
            p.ScheduleBuffer(buffer, () => this.Schedule(buffer, scratch, gen));
        }
        catch (Exception ex)
        {
            // The engine stopped underneath us (route change); the rebuild schedules new buffers.
            logger.LogDebug(ex, "Failed to schedule a duplex playback buffer");
        }
    }

    void OnConfigurationChange()
    {
        var lost = false;
        lock (this.graphGate)
        {
            if (!this.running || this.rebuilding || this.engine is not { } e)
                return;

            this.rebuilding = true;
            try
            {
#if MACOS
                // The chosen mic was unplugged: its unit has nothing to capture from, so start over on the
                // system default.
                if (this.InputDevice is { } chosen && MacAudioHardware.FindDevice(chosen.Id) == 0)
                {
                    logger.LogDebug("Duplex input {Device} went away; using the system default", chosen.Name);
                    this.TeardownEngine();
                    this.BuildEngine(null);
                    lost = true;
                }
                else
#endif
                {
                    // Stopping the player flushes its buffers; their completions are from the old generation
                    // and don't reschedule, so the restart below starts from a clean set.
                    this.generation++;
                    this.player?.Stop();
                    this.ConnectAndStart(e);
                }
#if !MACOS
                this.ApplySpeakerphone(AVAudioSession.SharedInstance());
#endif
                logger.LogDebug("Apple audio duplex rebuilt after a configuration change");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to rebuild the audio duplex after a configuration change");
            }
            finally
            {
                this.rebuilding = false;
            }
        }

        // Outside the lock: a handler may well set InputDevice again.
        if (lost)
            this.OnInputDeviceUnavailable();
    }

    byte[] Convert(AVAudioConverter conv, AVAudioPcmBuffer input)
    {
        var outFormat = this.captureFormat!;
        var capacity = (uint)(input.FrameLength * outFormat.SampleRate / input.Format.SampleRate) + 1024;
        using var output = new AVAudioPcmBuffer(outFormat, capacity);

        var supplied = false;
        var status = conv.ConvertToBuffer(output, out var error, (uint _, out AVAudioConverterInputStatus inputStatus) =>
        {
            // Hand the converter this one buffer, then report dry (it keeps its resampler state).
            if (supplied)
            {
                inputStatus = AVAudioConverterInputStatus.NoDataNow;
                return null!;
            }
            supplied = true;
            inputStatus = AVAudioConverterInputStatus.HaveData;
            return input;
        });

        if (status == AVAudioConverterOutputStatus.Error)
        {
            logger.LogDebug("Duplex capture conversion failed: {Error}", error?.LocalizedDescription);
            return [];
        }

        var frames = (int)output.FrameLength;
        if (frames == 0)
            return [];

        var data = new byte[frames * 2];
        Marshal.Copy(Marshal.ReadIntPtr(output.Int16ChannelData), data, 0, data.Length);
        return data;
    }

#if !MACOS
    AVAudioSessionCategoryOptions CategoryOptions()
    {
        AVAudioSessionCategoryOptions opts = 0;
        // HFP: a Bluetooth headset is both the mic and the speaker of a call. (A2DP is output-only.)
        if (this.Options.Processing?.AllowBluetooth != false)
            opts |= AVAudioSessionCategoryOptions.AllowBluetooth;
        if (this.speakerphone)
            opts |= AVAudioSessionCategoryOptions.DefaultToSpeaker;
        return opts;
    }

    // DefaultToSpeaker only replaces the earpiece; a headset keeps the audio. The explicit override
    // covers the case where the route already sits on the receiver.
    void ApplySpeakerphone(AVAudioSession session) => this.ApplySpeakerphone(session, this.InputDevice);

    void ApplySpeakerphone(AVAudioSession session, AudioDevice? input)
    {
        var current = session.CurrentRoute?.Outputs?.FirstOrDefault()?.PortType;
        var onBuiltIn = current == null
            || current == AVAudioSession.PortBuiltInReceiver
            || current == AVAudioSession.PortBuiltInSpeaker;

        // A chosen headset mic brings its own speaker: don't force the call off it onto the loudspeaker.
        var headsetMic = input is { Type: not (AudioDeviceType.BuiltInMic or AudioDeviceType.Unknown) };

        var over = this.speakerphone && onBuiltIn && !headsetMic
            ? AVAudioSessionPortOverride.Speaker
            : AVAudioSessionPortOverride.None;
        session.OverrideOutputAudioPort(over, out _);
    }

    // The chosen port as the session's preferred input (null clears the preference, back to the default
    // route). Returns false, on the default, when the port isn't available.
    bool ApplyPreferredInput(AVAudioSession session, AudioDevice? device)
    {
        AVAudioSessionPortDescription? port = null;
        if (device != null)
        {
            port = session.AvailableInputs?.FirstOrDefault(p => p.UID == device.Id);
            if (port == null)
                logger.LogDebug("Duplex input {Device} is not available; using the default route", device.Name);
        }

        if (!session.SetPreferredInput(port, out var err))
        {
            logger.LogWarning("Failed to set the preferred input to {Device}: {Error}", device?.Name ?? "the default", err?.LocalizedDescription);
            return false;
        }
        return device == null || port != null;
    }

    void OnRouteChange()
    {
        if (!this.running || this.InputDevice is not { } chosen)
            return;

        var session = AVAudioSession.SharedInstance();
        if (session.AvailableInputs?.Any(p => p.UID == chosen.Id) == true)
            return;

        logger.LogDebug("Duplex input {Device} went away; using the default route", chosen.Name);
        session.SetPreferredInput(null, out _);
        this.OnInputDeviceUnavailable();
    }
#else
    // Point the input node's I/O unit at the chosen mic, or the system default for null. Returns false,
    // on the default, when that mic is no longer connected.
    bool SelectMacInput(AVAudioInputNode inputNode, AudioDevice? device)
    {
        // no choice: leave the unit on the system default it already follows. Setting the default explicitly on the
        // voice-processing unit can make it fail to initialize (-10875).
        if (device == null)
            return true;

        var deviceId = device == null ? MacAudioHardware.GetDefaultInputId() : MacAudioHardware.FindDevice(device.Id);
        var applied = true;
        if (deviceId == 0 && device != null)
        {
            logger.LogDebug("Duplex input {Device} is no longer connected; using the system default", device.Name);
            deviceId = MacAudioHardware.GetDefaultInputId();
            applied = false;
        }
        if (deviceId == 0)
            return applied && device == null;

        var status = inputNode.AudioUnit?.SetCurrentDevice(deviceId, AudioUnit.AudioUnitScopeType.Global);
        if (status != AudioUnit.AudioUnitStatus.OK)
        {
            logger.LogWarning("Failed to select duplex input {Device} ({Status}); using the system default", device?.Name ?? "default", status);
            return device == null;
        }
        return applied;
    }
#endif

    protected override Task StopCoreAsync()
    {
        lock (this.graphGate)
        {
            this.running = false;
            this.TeardownEngine();
        }

#if !MACOS
        this.routeObserver?.Dispose();
        this.routeObserver = null;

        var session = AVAudioSession.SharedInstance();
        session.OverrideOutputAudioPort(AVAudioSessionPortOverride.None, out _);
        session.SetPreferredInput(this.priorPreferredInput, out _);
        if (this.priorCategory != null)
        {
            session.SetCategory(new NSString(this.priorCategory), this.priorOptions, out _);
            if (this.priorMode != null)
                session.SetMode(new NSString(this.priorMode), out _);
        }
        if (this.priorIoBufferDuration > 0)
            session.SetPreferredIOBufferDuration(this.priorIoBufferDuration, out _);
        session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
#endif

        logger.LogDebug("Apple audio duplex stopped");
        return Task.CompletedTask;
    }
}
