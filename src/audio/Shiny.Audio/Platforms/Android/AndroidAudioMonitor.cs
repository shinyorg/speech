using Android;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.Audiofx;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

public class AndroidAudioMonitor(AndroidPlatform platform, ILogger<AndroidAudioMonitor> logger) : IAudioMonitor
{
    const int SampleRate = 16000;

    AudioRecord? record;
    AudioTrack? track;
    CancellationTokenSource? cts;
    AcousticEchoCanceler? echoCanceler;
    NoiseSuppressor? noiseSuppressor;
    AutomaticGainControl? gainControl;
    AudioManager? audioManager;
    AudioFocusRequestClass? focusRequest;
    double gain = 1.0;

    public bool IsMonitoring { get; private set; }

    public event EventHandler<double>? InputLevelChanged;

    public double Gain
    {
        get => gain;
        set
        {
            gain = Math.Clamp(value, 0, 1);
            track?.SetVolume((float)gain);
        }
    }

    public Task<AccessState> RequestAccess()
        => platform.RequestAccess(Manifest.Permission.RecordAudio);

    public Task Start(AudioMonitorOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (IsMonitoring)
            return Task.CompletedTask;

        options ??= new AudioMonitorOptions();
        gain = Math.Clamp(options.Gain, 0, 1);

        if (Android.App.Application.Context.CheckSelfPermission(Manifest.Permission.RecordAudio) != Permission.Granted)
            throw new InvalidOperationException("RECORD_AUDIO permission has not been granted. Call RequestAccess() first.");

        if (options.DuckOtherAudio)
            RequestDuckFocus();

        const ChannelIn inConfig = ChannelIn.Mono;
        const ChannelOut outConfig = ChannelOut.Mono;
        const Encoding encoding = Encoding.Pcm16bit;

        var minRec = AudioRecord.GetMinBufferSize(SampleRate, inConfig, encoding);
        var minTrack = AudioTrack.GetMinBufferSize(SampleRate, outConfig, encoding);
        var bufferSize = Math.Max(Math.Max(minRec, minTrack), 4096);

        // VoiceCommunication engages the platform AEC/NS/AGC chain — the feedback defense.
        var captureSource = options.Processing?.EchoCancellation == true
            ? AudioSource.VoiceCommunication
            : AudioSource.Mic;

        record = new AudioRecord(captureSource, SampleRate, inConfig, encoding, bufferSize);
        if (record.State != State.Initialized)
            throw new InvalidOperationException($"Failed to initialize AudioRecord (state={record.State}). The mic may be in use.");

        track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()!
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!)
            .SetAudioFormat(new AudioFormat.Builder()!
                .SetEncoding(encoding)!
                .SetSampleRate(SampleRate)!
                .SetChannelMask(outConfig)!
                .Build()!)
            .SetBufferSizeInBytes(bufferSize)
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();

        if (options.Processing?.AnyEnabled == true)
            AttachAudioEffects(record.AudioSessionId, options.Processing);

        if (options.InputDevice != null)
            SetPreferred(record, options.InputDevice.Id);
        if (options.OutputDevice != null)
            SetPreferred(track, options.OutputDevice.Id);

        track.SetVolume((float)gain);

        cts = new CancellationTokenSource();
        var token = cts.Token;
        var rec = record;
        var trk = track;

        record.StartRecording();
        track.Play();

        _ = Task.Run(() =>
        {
            var buffer = new byte[bufferSize];
            while (!token.IsCancellationRequested)
            {
                var read = rec.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    try
                    {
                        trk.Write(buffer, 0, read);
                    }
                    catch (Java.Lang.IllegalStateException)
                    {
                        break; // track released on the Stop path
                    }
                    InputLevelChanged?.Invoke(this, AudioLevel.FromPcm16(buffer.AsSpan(0, read)));
                }
            }
        }, token);

        IsMonitoring = true;
        logger.LogDebug("Android audio monitor started");
        return Task.CompletedTask;
    }

    public Task SetInputDevice(AudioDevice? device)
    {
        if (record != null)
            record.SetPreferredDevice(device == null ? null : FindDeviceInfo(GetDevicesTargets.Inputs, device.Id));
        return Task.CompletedTask;
    }

    public Task SetOutputDevice(AudioDevice? device)
    {
        if (track != null)
            track.SetPreferredDevice(device == null ? null : FindDeviceInfo(GetDevicesTargets.Outputs, device.Id));
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = null;

        ReleaseAudioEffects();

        if (record != null)
        {
            if (record.RecordingState == RecordState.Recording)
                record.Stop();
            record.Release();
            record = null;
        }

        if (track != null)
        {
            if (track.PlayState == PlayState.Playing)
                track.Stop();
            track.Release();
            track = null;
        }

        AbandonDuckFocus();   // restore other audio (music) to full volume

        IsMonitoring = false;
        logger.LogDebug("Android audio monitor stopped");
        return Task.CompletedTask;
    }

    // Take transient "may duck" focus so other audio (e.g. music) plays at a reduced volume under
    // the live mic instead of being interrupted; abandoned on Stop to restore it.
    void RequestDuckFocus()
    {
        audioManager ??= (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);
        if (audioManager == null)
            return;

        var attrs = new AudioAttributes.Builder()!
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Speech)!
            .Build()!;

        focusRequest = new AudioFocusRequestClass.Builder(AudioFocus.GainTransientMayDuck)!
            .SetAudioAttributes(attrs)!
            .SetOnAudioFocusChangeListener(new NoopFocusListener())!
            .Build();

        audioManager.RequestAudioFocus(focusRequest!);
    }

    void AbandonDuckFocus()
    {
        if (audioManager != null && focusRequest != null)
            audioManager.AbandonAudioFocusRequest(focusRequest);
        focusRequest?.Dispose();
        focusRequest = null;
    }

    sealed class NoopFocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange) { }
    }

    public async ValueTask DisposeAsync()
    {
        await Stop();
        GC.SuppressFinalize(this);
    }

    static void SetPreferred(AudioRecord rec, string deviceId)
    {
        var info = FindDeviceInfo(GetDevicesTargets.Inputs, deviceId);
        if (info != null)
            rec.SetPreferredDevice(info);
    }

    static void SetPreferred(AudioTrack trk, string deviceId)
    {
        var info = FindDeviceInfo(GetDevicesTargets.Outputs, deviceId);
        if (info != null)
            trk.SetPreferredDevice(info);
    }

    static AudioDeviceInfo? FindDeviceInfo(GetDevicesTargets target, string id)
    {
        var am = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);
        var devices = am?.GetDevices(target);
        if (devices == null)
            return null;

        foreach (var d in devices)
        {
            if (d.Id.ToString() == id)
                return d;
        }
        return null;
    }

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
}
