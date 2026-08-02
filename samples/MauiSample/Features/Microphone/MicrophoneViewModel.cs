using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.Audio;

namespace MauiSample.Features.Microphone;

[ShellMap<MicrophonePage>("Microphone")]
public partial class MicrophoneViewModel : ObservableObject
{
    readonly IAudio audio;
    readonly IAudioMonitor monitor;
    readonly IAudioDevices devices;

    // Inject the single IAudio facade and reach the pieces through it (audio.Monitor / audio.Devices /
    // audio.Source / audio.Player), rather than taking each focused service as its own dependency.
    public MicrophoneViewModel(IAudio audio)
    {
        this.audio = audio;
        this.monitor = audio.Monitor;
        this.devices = audio.Devices;

        monitor.InputLevelChanged += OnLevel;
        devices.Changed += OnDevicesChanged;

        // Add returns the instance, so each effect stays addressable for the live controls below.
        // They start disabled — toggling one crossfades it in rather than clicking.
        pitch = effects.Add(new PitchShiftEffect { Enabled = false });
        echo = effects.Add(new EchoEffect { Enabled = false });
        reverb = effects.Add(new ReverbEffect { Enabled = false });
        robot = effects.Add(new RingModEffect { Enabled = false });

        RefreshRoutes();
    }

    // ── Live monitor (mic → output) ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonitorButtonText))]
    bool isMonitoring;

    public string MonitorButtonText => IsMonitoring ? "■  Stop Talking" : "🎙  Start Talking";

    [ObservableProperty]
    string statusText = "Connect a Bluetooth speaker (or use the phone), then Start Talking.";

    [ObservableProperty]
    double level;

    [ObservableProperty]
    double gain = 1.0;

    // Voice processing (AEC/NS/AGC) fights feedback howl, BUT on iOS it forces Bluetooth onto the
    // low-quality HFP profile — which a Bluetooth *speaker* (A2DP) can't provide, so audio falls back
    // to the phone. Default OFF so live monitoring routes to a Bluetooth speaker; enable it only when
    // outputting to the phone speaker and feedback is a problem. Takes effect on the next Start.
    [ObservableProperty]
    bool echoCancellation;

    [ObservableProperty]
    bool noiseSuppression;

    [ObservableProperty]
    bool automaticGainControl;

    partial void OnGainChanged(double value) => monitor.Gain = value;

    [RelayCommand]
    async Task ToggleMonitor()
    {
        if (IsMonitoring)
        {
            await monitor.Stop();
            IsMonitoring = false;
            Level = 0;
            StatusText = "Stopped.";
            RefreshRoutes();
            return;
        }

        var access = await monitor.RequestAccess();
        if (access != AccessState.Available)
        {
            StatusText = $"Microphone access: {access}";
            return;
        }

        try
        {
            await monitor.Start(new AudioMonitorOptions
            {
                Gain = Gain,
                Processing = (EchoCancellation || NoiseSuppression || AutomaticGainControl)
                    ? new AudioProcessingOptions
                    {
                        EchoCancellation = EchoCancellation,
                        NoiseSuppression = NoiseSuppression,
                        AutomaticGainControl = AutomaticGainControl
                    }
                    : null
            });
            IsMonitoring = true;
            StatusText = "Live — your voice is playing to the output below.";
            RefreshRoutes();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    // Called by the page when it disappears so we never leave the mic open (feedback / battery).
    public async Task StopIfRunning()
    {
        if (IsMonitoring)
        {
            await monitor.Stop();
            IsMonitoring = false;
            Level = 0;
        }

        if (IsRecording)
            await StopRecording();
    }

    // ── Current routes (display only — see notes below) ───────────────────────────────

    [ObservableProperty] string currentInputText = "—";
    [ObservableProperty] string currentOutputText = "—";

    void RefreshRoutes()
    {
        CurrentInputText = Describe(devices.CurrentInput);
        CurrentOutputText = Describe(devices.CurrentOutput);
    }

    // Friendly "Name · Connection" so you can tell at a glance whether output is on the phone, a
    // Bluetooth speaker, wired, etc.
    static string Describe(AudioDevice? d)
    {
        if (d == null)
            return "—";

        var kind = d.Type switch
        {
            AudioDeviceType.BluetoothA2dp or AudioDeviceType.Bluetooth => "Bluetooth",
            AudioDeviceType.AirPlay => "AirPlay",
            AudioDeviceType.BuiltInSpeaker => "Built-in speaker",
            AudioDeviceType.BuiltInReceiver => "Earpiece",
            AudioDeviceType.BuiltInMic => "Built-in mic",
            AudioDeviceType.WiredHeadphones or AudioDeviceType.WiredHeadset => "Wired",
            AudioDeviceType.Usb => "USB",
            AudioDeviceType.CarAudio => "Car audio",
            AudioDeviceType.Hdmi => "HDMI",
            _ => d.Type.ToString()
        };
        return $"{d.Name} · {kind}";
    }

    // ── Effects (live DSP applied to capture — drive these while recording) ──────────

    // One chain, built once and kept for the lifetime of the page. Every control below mutates an
    // effect object directly: the audio thread picks the change up on the next buffer, so there is
    // nothing to "apply" and no need to restart the recording.
    readonly AudioEffectChain effects = new();
    readonly PitchShiftEffect pitch;
    readonly EchoEffect echo;
    readonly ReverbEffect reverb;
    readonly RingModEffect robot;

    [ObservableProperty]
    bool effectsEnabled = true;

    [ObservableProperty]
    double pitchSemitones;

    [ObservableProperty]
    bool pitchEnabled;

    [ObservableProperty]
    bool echoEnabled;

    [ObservableProperty]
    double echoDelayMs = 250;

    [ObservableProperty]
    double echoMix = 0.35;

    [ObservableProperty]
    bool reverbEnabled;

    [ObservableProperty]
    double reverbRoomSize = 0.6;

    [ObservableProperty]
    double reverbMix = 0.35;

    [ObservableProperty]
    bool robotEnabled;

    [ObservableProperty]
    double robotFrequency = 45;

    partial void OnEffectsEnabledChanged(bool value) => effects.Enabled = value;

    partial void OnPitchEnabledChanged(bool value) => pitch.Enabled = value;
    partial void OnPitchSemitonesChanged(double value) => pitch.Semitones = (float)value;

    partial void OnEchoEnabledChanged(bool value) => echo.Enabled = value;
    partial void OnEchoDelayMsChanged(double value) => echo.DelayMs = (float)value;
    partial void OnEchoMixChanged(double value) => echo.Mix = (float)value;

    partial void OnReverbEnabledChanged(bool value) => reverb.Enabled = value;
    partial void OnReverbRoomSizeChanged(double value) => reverb.RoomSize = (float)value;
    partial void OnReverbMixChanged(double value) => reverb.Mix = (float)value;

    partial void OnRobotEnabledChanged(bool value) => robot.Enabled = value;
    partial void OnRobotFrequencyChanged(double value) => robot.Frequency = (float)value;

    // ── Record & Play (record with the mic, then play the clip back) ──────────────────

    IAudioRecorder? recorder;
    AudioRecording? lastRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonText))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanPlayDry))]
    [NotifyPropertyChangedFor(nameof(CanChangeRecordMode))]
    bool isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanPlayDry))]
    bool hasRecording;

    [ObservableProperty]
    string recordStatus = "Record a clip, then play it back through the current output.";

    /// <summary>Wet (processed), Dry (raw mic), or Both — two files, so the takes can be compared.</summary>
    public IReadOnlyList<string> RecordModes { get; } = ["Wet", "Dry", "Both"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayDry))]
    string selectedRecordMode = "Wet";

    public string RecordButtonText => IsRecording ? "■  Stop" : "⏺  Record";
    public bool CanPlay => HasRecording && !IsRecording;
    public bool CanPlayDry => CanPlay && lastRecording?.DryPath != null;

    // The mode is fixed for the duration of a take — it decides which files get opened at Start.
    public bool CanChangeRecordMode => !IsRecording;

    [RelayCommand]
    async Task ToggleRecord()
    {
        if (IsRecording)
        {
            await StopRecording();
            return;
        }

        if (monitor.IsMonitoring)   // live monitor shares the mic/session — stop it first
        {
            await monitor.Stop();
            IsMonitoring = false;
        }

        recorder = audio.Recorder;

        if (await recorder.RequestAccess() != AccessState.Available)
        {
            RecordStatus = "Microphone access denied.";
            return;
        }

        recorder.InputLevelChanged += OnLevel;

        var mode = SelectedRecordMode switch
        {
            "Dry" => AudioRecordMode.Dry,
            "Both" => AudioRecordMode.Both,
            _ => AudioRecordMode.Wet
        };

        try
        {
            await recorder.StartAsync(new AudioRecordingOptions
            {
                Mode = mode,
                Effects = effects
            });
        }
        catch (Exception ex)
        {
            recorder.InputLevelChanged -= OnLevel;
            RecordStatus = $"Error: {ex.Message}";
            return;
        }

        HasRecording = false;
        IsRecording = true;
        RecordStatus = "Recording… change the effects below while it runs.";
    }

    async Task StopRecording()
    {
        IsRecording = false;

        if (recorder == null)
            return;

        recorder.InputLevelChanged -= OnLevel;
        lastRecording = await recorder.StopAsync();

        await recorder.DisposeAsync();
        recorder = null;
        Level = 0;

        if (lastRecording == null)
        {
            RecordStatus = "Nothing was recorded.";
            return;
        }

        HasRecording = true;
        RecordStatus = lastRecording.DryPath == null
            ? $"Recorded {lastRecording.Duration.TotalSeconds:0.0}s. Tap Play."
            : $"Recorded {lastRecording.Duration.TotalSeconds:0.0}s — wet and dry. Play either.";
    }

    [RelayCommand]
    Task Play() => PlayFile(lastRecording?.Path);

    [RelayCommand]
    Task PlayDry() => PlayFile(lastRecording?.DryPath);

    async Task PlayFile(string? path)
    {
        if (path == null)
            return;

        try
        {
            // Playback runs in a Playback session and follows the current output route
            // (built-in / Bluetooth / wired).
            await audio.Player.PlayAsync(path);
        }
        catch (Exception ex)
        {
            RecordStatus = $"Playback error: {ex.Message}";
            return;
        }

        RefreshRoutes();   // CurrentOutput now reflects where the clip actually played
        RecordStatus = $"Played to {CurrentOutputText}.";
    }

    void OnLevel(object? sender, double value)
        => MainThread.BeginInvokeOnMainThread(() => Level = value);

    void OnDevicesChanged(object? sender, EventArgs e)
        => MainThread.BeginInvokeOnMainThread(RefreshRoutes);
}
