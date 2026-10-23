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
        return Task.FromResult(AccessState.Available);
#else
        var tcs = new TaskCompletionSource<AccessState>();
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
    volatile bool running;
    volatile int generation;   // bumped on every (re)start, so completions from a flushed player don't reschedule
    bool rebuilding;
    bool speakerphone = options.Speakerphone;

#if !MACOS
    string? priorCategory;
    AVAudioSessionCategoryOptions priorOptions;
    string? priorMode;
    double priorIoBufferDuration;
#endif

    bool WantsProcessing => this.Options.Processing?.AnyEnabled == true;

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

        this.ApplySpeakerphone(session);
#endif

        var e = new AVAudioEngine();
        this.engine = e;

        // Voice processing has to be enabled before the engine is prepared, and it swaps the I/O unit
        // under BOTH the input and output nodes: that's what gives the canceller its playback reference.
        if (this.WantsProcessing && !e.InputNode.SetVoiceProcessingEnabled(true, out var vpErr))
            logger.LogWarning("Failed to enable voice processing (AEC/NS/AGC): {Error}", vpErr?.LocalizedDescription);

        this.captureFormat = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, rate, 1, false);
        this.playFormat = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, rate, 1, false);

        var p = new AVAudioPlayerNode();
        this.player = p;
        e.AttachNode(p);

        this.running = true;
        this.ConnectAndStart(e);

        // A route change (headset plugged in, Bluetooth connected, speaker toggled) stops the engine and
        // can change the input format. Rebuild so the call keeps going instead of going silent.
        this.configObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            AVAudioEngine.ConfigurationChangeNotification,
            _ => this.OnConfigurationChange(),
            e
        );

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
        if (!this.running || this.rebuilding || this.engine is not { } e)
            return;

        this.rebuilding = true;
        try
        {
            // Stopping the player flushes its buffers; their completions are from the old generation and
            // don't reschedule, so the restart below starts from a clean set.
            this.generation++;
            this.player?.Stop();
            this.ConnectAndStart(e);
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
    void ApplySpeakerphone(AVAudioSession session)
    {
        var current = session.CurrentRoute?.Outputs?.FirstOrDefault()?.PortType;
        var onBuiltIn = current == null
            || current == AVAudioSession.PortBuiltInReceiver
            || current == AVAudioSession.PortBuiltInSpeaker;

        var over = this.speakerphone && onBuiltIn
            ? AVAudioSessionPortOverride.Speaker
            : AVAudioSessionPortOverride.None;
        session.OverrideOutputAudioPort(over, out _);
    }
#endif

    protected override Task StopCoreAsync()
    {
        this.running = false;

        if (this.configObserver != null)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(this.configObserver);
            this.configObserver = null;
        }

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

#if !MACOS
        var session = AVAudioSession.SharedInstance();
        session.OverrideOutputAudioPort(AVAudioSessionPortOverride.None, out _);
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
