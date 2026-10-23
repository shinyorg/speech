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
                this.ApplyRoute();
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

        this.ApplyRoute();

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

    // Headsets win; otherwise the loudspeaker or the earpiece, per Speakerphone.
    void ApplyRoute()
    {
        var am = this.audioManager;
        if (am == null)
            return;

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var devices = am.AvailableCommunicationDevices;
                var pick = devices.FirstOrDefault(d => this.IsHeadset(d.Type))
                    ?? devices.FirstOrDefault(d => d.Type == (this.speakerphone ? DroidDeviceType.BuiltinSpeaker : DroidDeviceType.BuiltinEarpiece));

                if (pick == null)
                    am.ClearCommunicationDevice();
                else if (!am.SetCommunicationDevice(pick))
                    logger.LogWarning("Failed to route the call to {Device}", pick.Type);
            }
            else
            {
                var outputs = am.GetDevices(GetDevicesTargets.Outputs) ?? [];
#pragma warning disable CA1422
                am.SpeakerphoneOn = this.speakerphone && !outputs.Any(d => this.IsHeadset(d.Type));
#pragma warning restore CA1422
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply the duplex audio route (speakerphone={Speaker})", this.speakerphone);
        }
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
        this.running = false;

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
