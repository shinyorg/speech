using Android;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.Audiofx;
using Microsoft.Extensions.Logging;
using Shiny.Audio.Duplex;
using DroidDeviceType = Android.Media.AudioDeviceType;

namespace Shiny.Audio;

/// <summary>
/// <see cref="IAudioDuplex"/> on Android: a <c>VOICE_COMMUNICATION</c> <see cref="AudioRecord"/> and a
/// streaming <c>USAGE_VOICE_COMMUNICATION</c> <see cref="AudioTrack"/> on the same audio session, with
/// the platform's echo canceller, noise suppressor and AGC attached, while the device is in
/// <c>MODE_IN_COMMUNICATION</c>.
/// </summary>
/// <remarks>Requires <c>RECORD_AUDIO</c> and <c>MODIFY_AUDIO_SETTINGS</c> in the manifest.</remarks>
public class AndroidAudioDuplex(AndroidPlatform platform, ILogger<AndroidAudioDuplex> logger) : IAudioDuplex
{
    readonly AudioDuplexSlot slot = new();

    public Task<AccessState> RequestAccess()
        => platform.RequestAccess(Manifest.Permission.RecordAudio);

    public Task<IAudioDuplexSession> StartAsync(AudioDuplexOptions? options = null, CancellationToken cancellationToken = default)
        => this.slot.StartAsync(options, cancellationToken, (opts, ct) =>
        {
            if (Android.App.Application.Context.CheckSelfPermission(Manifest.Permission.RecordAudio) != Permission.Granted)
                throw new InvalidOperationException("RECORD_AUDIO permission has not been granted. Call RequestAccess() first.");

            var session = new AndroidAudioDuplexSession(opts, logger);
            try
            {
                session.Start();
            }
            catch
            {
                // Put the audio mode and routing back before reporting the failure.
                _ = session.StopAsync();
                throw;
            }
            return Task.FromResult<AudioDuplexSession>(session);
        });
}


sealed class AndroidAudioDuplexSession(AudioDuplexOptions options, ILogger logger) : AudioDuplexSession(options)
{
    AudioManager? audioManager;
    AudioRecord? record;
    AudioTrack? track;
    AcousticEchoCanceler? echoCanceler;
    NoiseSuppressor? noiseSuppressor;
    AutomaticGainControl? gainControl;
    AudioFocusRequestClass? focusRequest;
    Thread? captureThread;
    Thread? playbackThread;
    InputDeviceCallback? deviceCallback;
    AudioDeviceInfo? inputInfo;              // the chosen mic, resolved; null = the default
    readonly object routeGate = new();       // the record's preferred device and the call route move together
    volatile bool running;
    bool speakerphone = options.Speakerphone;
    bool modeChanged;
    Mode priorMode;
    bool priorSpeakerphone;

    bool WantsProcessing => this.Options.Processing?.AnyEnabled == true;
    bool AllowBluetooth => this.Options.Processing?.AllowBluetooth != false;

    public override bool Speakerphone
    {
        get => this.speakerphone;
        set
        {
            if (this.speakerphone == value)
                return;

            this.speakerphone = value;
            if (this.running)
            {
                lock (this.routeGate)
                    this.ApplyRoute();
            }
        }
    }

    // SetPreferredDevice reroutes a recording AudioRecord in place: no restart, no gap beyond the HAL's
    // own switch. The call route follows the mic (see ApplyRoute).
    protected override bool SelectInputDevice(AudioDevice? device)
    {
        lock (this.routeGate)
        {
            if (!this.running || this.record is not { } rec)
                return true;

            var applied = this.ApplyPreferredInput(rec, device);
            this.ApplyRoute();
            logger.LogDebug("Android audio duplex input set to {Device}", this.inputInfo?.ProductName ?? "the default");
            return applied;
        }
    }

    // The chosen mic as the record's preferred device (null clears it). Returns false, on the default,
    // when that mic isn't connected.
    bool ApplyPreferredInput(AudioRecord rec, AudioDevice? device)
    {
        var info = device == null ? null : AndroidAudioMonitor.FindDeviceInfo(GetDevicesTargets.Inputs, device.Id);
        if (device != null && info == null)
            logger.LogDebug("Duplex input {Device} is not available; using the default", device.Name);

        this.inputInfo = info;
        if (!rec.SetPreferredDevice(info))
            logger.LogWarning("Failed to set the duplex input to {Device}", device?.Name ?? "the default");

        return device == null || info != null;
    }

    // The chosen mic was unplugged or disconnected: back to the default mic and the default route.
    void OnDevicesRemoved(AudioDeviceInfo[] removed)
    {
        lock (this.routeGate)
        {
            if (!this.running || this.inputInfo is not { } mic || !removed.Any(d => d.Id == mic.Id))
                return;

            logger.LogDebug("Duplex input {Device} went away; using the default", mic.ProductName);
            this.inputInfo = null;
            this.record?.SetPreferredDevice(null);
            this.ApplyRoute();
        }
        this.OnInputDeviceUnavailable();
    }

    sealed class InputDeviceCallback(Action<AudioDeviceInfo[]> onRemoved) : AudioDeviceCallback
    {
        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices)
        {
            if (removedDevices is { Length: > 0 })
                onRemoved(removedDevices);
        }
    }

    public void Start()
    {
        var rate = this.Options.SampleRate;
        var frameBytes = this.Options.FrameBytes;
        const ChannelIn inConfig = ChannelIn.Mono;
        const ChannelOut outConfig = ChannelOut.Mono;
        const Encoding encoding = Encoding.Pcm16bit;

        this.audioManager = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService)
            ?? throw new InvalidOperationException("AudioManager is not available.");

        // Call mode first: it selects the device's voice-call audio path (and its routing rules) that the
        // record/track below are opened on.
        this.priorMode = this.audioManager.Mode;
#pragma warning disable CA1422 // SpeakerphoneOn is the only way to route below API 31
        this.priorSpeakerphone = this.audioManager.SpeakerphoneOn;
#pragma warning restore CA1422
        this.audioManager.Mode = Mode.InCommunication;
        this.modeChanged = true;
        this.RequestFocus();

        var minRec = AudioRecord.GetMinBufferSize(rate, inConfig, encoding);
        var minTrack = AudioTrack.GetMinBufferSize(rate, outConfig, encoding);

        // VOICE_COMMUNICATION is the capture source the device's own AEC is tuned for, and the one that
        // gets the playback reference from the voice-communication track.
        var source = this.WantsProcessing ? AudioSource.VoiceCommunication : AudioSource.Mic;
        this.record = new AudioRecord(source, rate, inConfig, encoding, Math.Max(minRec, frameBytes * 4));
        if (this.record.State != State.Initialized)
            throw new InvalidOperationException($"Failed to initialize AudioRecord (state={this.record.State}). The mic may be in use.");

        this.track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()!
                .SetUsage(AudioUsageKind.VoiceCommunication)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!)
            .SetAudioFormat(new AudioFormat.Builder()!
                .SetEncoding(encoding)!
                .SetSampleRate(rate)!
                .SetChannelMask(outConfig)!
                .Build()!)
            // A small hardware buffer: the jitter buffer in front of it does the smoothing.
            .SetBufferSizeInBytes(Math.Max(minTrack, frameBytes * 2))
            .SetTransferMode(AudioTrackMode.Stream)
            .SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)
            .SetSessionId(this.record.AudioSessionId)
            .Build();

        if (this.track.State != AudioTrackState.Initialized)
            throw new InvalidOperationException($"Failed to initialize AudioTrack (state={this.track.State}).");

        if (this.WantsProcessing)
            this.AttachEffects(this.record.AudioSessionId, this.Options.Processing!);

        // Android routes by preference: a chosen mic that has since gone away is simply not found, which
        // leaves the default (the same rule AndroidAudioSource follows).
        var inputApplied = this.ApplyPreferredInput(this.record, this.InputDevice);
        this.ApplyRoute();
        if (!inputApplied)
            this.OnInputDeviceUnavailable(notify: false);

        // Only removals matter: they're how a chosen headset mic disappears mid-call.
        this.deviceCallback = new InputDeviceCallback(this.OnDevicesRemoved);
        this.audioManager.RegisterAudioDeviceCallback(this.deviceCallback, null);

        this.running = true;
        this.record.StartRecording();
        this.track.Play();

        var rec = this.record;
        var trk = this.track;
        this.captureThread = new Thread(() => this.CaptureLoop(rec, frameBytes)) { IsBackground = true, Name = "Shiny.Audio duplex capture" };
        this.playbackThread = new Thread(() => this.PlaybackLoop(trk, frameBytes)) { IsBackground = true, Name = "Shiny.Audio duplex playback" };
        this.captureThread.Start();
        this.playbackThread.Start();

        logger.LogDebug(
            "Android audio duplex started ({Rate}Hz, session={Session}, AEC:{Aec} NS:{Ns} AGC:{Agc})",
            rate, this.record.AudioSessionId, this.echoCanceler != null, this.noiseSuppressor != null, this.gainControl != null
        );
    }

    void CaptureLoop(AudioRecord rec, int frameBytes)
    {
        Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);
        var buffer = new byte[frameBytes];
        while (this.running)
        {
            int read;
            try
            {
                read = rec.Read(buffer, 0, buffer.Length);
            }
            catch (Exception)
            {
                break;   // released on the stop path
            }

            if (read > 0)
                this.OnCaptured(buffer.AsSpan(0, read));
            else if (read < 0)
                break;   // ERROR_INVALID_OPERATION / ERROR_DEAD_OBJECT: the record was stopped
        }
    }

    // A blocking write paces this loop at exactly the rate the track plays, so each pass pulls one frame
    // from the jitter buffer just in time.
    void PlaybackLoop(AudioTrack trk, int frameBytes)
    {
        Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);
        var buffer = new byte[frameBytes];
        while (this.running)
        {
            this.ReadPlayback(buffer);
            try
            {
                if (trk.Write(buffer, 0, buffer.Length) < 0)
                    break;
            }
            catch (Exception)
            {
                break;   // released on the stop path
            }
        }
    }

    // With no mic chosen: headsets win, otherwise the loudspeaker or the earpiece, per Speakerphone.
    // In MODE_IN_COMMUNICATION the communication device decides the call's mic as well as its speaker, so
    // a chosen mic steers it too: a headset mic routes the call to that headset (that's also what brings a
    // Bluetooth SCO link up), and the built-in mic keeps the call on the speaker/earpiece even with a
    // headset connected. A mic with no communication counterpart (a USB mic) leaves the default rule.
    void ApplyRoute()
    {
        var am = this.audioManager;
        if (am == null)
            return;

        var mic = this.inputInfo;
        var builtInMic = mic?.Type == DroidDeviceType.BuiltinMic;

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var devices = am.AvailableCommunicationDevices;
                var builtIn = devices.FirstOrDefault(d => d.Type == (this.speakerphone ? DroidDeviceType.BuiltinSpeaker : DroidDeviceType.BuiltinEarpiece));
                var pick = builtInMic
                    ? builtIn
                    : (mic == null ? null : devices.FirstOrDefault(d => SameHardware(d, mic)))
                        ?? devices.FirstOrDefault(d => this.IsHeadset(d.Type))
                        ?? builtIn;

                if (pick == null)
                    am.ClearCommunicationDevice();
                else if (!am.SetCommunicationDevice(pick))
                    logger.LogWarning("Failed to route the call to {Device}", pick.Type);
            }
            else
            {
                // Below 31 there's only the speakerphone switch; the record's preferred device picks the mic.
                var outputs = am.GetDevices(GetDevicesTargets.Outputs) ?? [];
                var headsetMic = mic != null && !builtInMic && outputs.Any(d => SameHardware(d, mic));
#pragma warning disable CA1422
                am.SpeakerphoneOn = this.speakerphone && !headsetMic && (builtInMic || !outputs.Any(d => this.IsHeadset(d.Type)));
#pragma warning restore CA1422
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply the duplex audio route (speakerphone={Speaker})", this.speakerphone);
        }
    }

    // The speaker side of the same physical headset as a chosen mic: same type, and same address where
    // the platform reports one (two Bluetooth headsets differ by address).
    static bool SameHardware(AudioDeviceInfo output, AudioDeviceInfo mic)
    {
        if (output.Type != mic.Type || mic.Type == DroidDeviceType.BuiltinMic)
            return false;

        if (!OperatingSystem.IsAndroidVersionAtLeast(28))
            return true;

        var a = output.Address;
        var b = mic.Address;
        return string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a == b;
    }

    bool IsHeadset(DroidDeviceType type)
    {
        if (type is DroidDeviceType.WiredHeadset or DroidDeviceType.WiredHeadphones or DroidDeviceType.UsbHeadset)
            return true;

        if (!this.AllowBluetooth)
            return false;

        return type == DroidDeviceType.BluetoothSco
            || (OperatingSystem.IsAndroidVersionAtLeast(31) && type == DroidDeviceType.BleHeadset);
    }

    // Transient exclusive focus, as a call takes: other media pauses for the call and resumes after it.
    void RequestFocus()
    {
        var attrs = new AudioAttributes.Builder()!
            .SetUsage(AudioUsageKind.VoiceCommunication)!
            .SetContentType(AudioContentType.Speech)!
            .Build()!;

        this.focusRequest = new AudioFocusRequestClass.Builder(AudioFocus.GainTransient)!
            .SetAudioAttributes(attrs)!
            .SetOnAudioFocusChangeListener(new NoopFocusListener())!
            .Build();

        this.audioManager!.RequestAudioFocus(this.focusRequest!);
    }

    sealed class NoopFocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange) { }
    }

    // Best-effort, like AndroidAudioSource: an effect the device lacks is skipped.
    void AttachEffects(int audioSessionId, AudioProcessingOptions processing)
    {
        if (processing.EchoCancellation && AcousticEchoCanceler.IsAvailable)
        {
            this.echoCanceler = AcousticEchoCanceler.Create(audioSessionId);
            this.echoCanceler?.SetEnabled(true);
        }
        if (processing.NoiseSuppression && NoiseSuppressor.IsAvailable)
        {
            this.noiseSuppressor = NoiseSuppressor.Create(audioSessionId);
            this.noiseSuppressor?.SetEnabled(true);
        }
        if (processing.AutomaticGainControl && AutomaticGainControl.IsAvailable)
        {
            this.gainControl = AutomaticGainControl.Create(audioSessionId);
            this.gainControl?.SetEnabled(true);
        }
    }

    protected override Task StopCoreAsync()
    {
        lock (this.routeGate)
            this.running = false;

        if (this.deviceCallback != null)
        {
            try { this.audioManager?.UnregisterAudioDeviceCallback(this.deviceCallback); } catch { }
            this.deviceCallback.Dispose();
            this.deviceCallback = null;
        }
        this.inputInfo = null;

        // Stopping unblocks the threads' Read/Write; join them before releasing what they use.
        try { if (this.record?.RecordingState == RecordState.Recording) this.record.Stop(); } catch { }
        try
        {
            if (this.track?.PlayState == PlayState.Playing)
            {
                this.track.Pause();
                this.track.Flush();
                this.track.Stop();
            }
        }
        catch { }

        this.captureThread?.Join(500);
        this.playbackThread?.Join(500);
        this.captureThread = null;
        this.playbackThread = null;

        this.echoCanceler?.Release();
        this.echoCanceler = null;
        this.noiseSuppressor?.Release();
        this.noiseSuppressor = null;
        this.gainControl?.Release();
        this.gainControl = null;

        this.record?.Release();
        this.record = null;
        this.track?.Release();
        this.track = null;

        if (this.audioManager is { } am)
        {
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                    am.ClearCommunicationDevice();
                else
#pragma warning disable CA1422
                    am.SpeakerphoneOn = this.priorSpeakerphone;
#pragma warning restore CA1422

                if (this.modeChanged)
                    am.Mode = this.priorMode;

                if (this.focusRequest != null)
                    am.AbandonAudioFocusRequest(this.focusRequest);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to restore the audio mode after the duplex session");
            }
        }
        this.focusRequest?.Dispose();
        this.focusRequest = null;

        logger.LogDebug("Android audio duplex stopped");
        return Task.CompletedTask;
    }
}
