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

    // ── Record & Play (record with the mic, then play the clip back) ──────────────────

    IAudioSource? captureSource;
    MemoryStream? captureBuffer;
    CancellationTokenSource? captureCts;
    Task? drainTask;
    byte[]? lastRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonText))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    bool isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    bool hasRecording;

    [ObservableProperty]
    string recordStatus = "Record a clip, then play it back through the current output.";

    public string RecordButtonText => IsRecording ? "■  Stop" : "⏺  Record";
    public bool CanPlay => HasRecording && !IsRecording;

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

        if (await monitor.RequestAccess() != AccessState.Available)
        {
            RecordStatus = "Microphone access denied.";
            return;
        }

        captureSource = audio.Source;
        captureBuffer = new MemoryStream();
        captureCts = new CancellationTokenSource();

        var stream = await captureSource.StartCaptureAsync(cancellationToken: captureCts.Token);
        drainTask = DrainAsync(stream, captureBuffer, captureCts.Token);

        HasRecording = false;
        IsRecording = true;
        RecordStatus = "Recording… tap Stop when done.";
    }

    async Task DrainAsync(Stream stream, MemoryStream target, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                    break;
                target.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    async Task StopRecording()
    {
        IsRecording = false;
        captureCts?.Cancel();

        try { if (captureSource != null) await captureSource.StopCaptureAsync(); } catch { }
        if (drainTask != null) { try { await drainTask; } catch { } }

        var pcm = captureBuffer?.ToArray() ?? [];
        if (captureSource != null) { await captureSource.DisposeAsync(); captureSource = null; }
        captureBuffer?.Dispose();
        captureBuffer = null;

        if (pcm.Length == 0)
        {
            RecordStatus = "Nothing was recorded.";
            return;
        }

        lastRecording = WavFromPcm16(pcm);
        HasRecording = true;
        var secs = pcm.Length / (16000 * 2.0);
        RecordStatus = $"Recorded {secs:0.0}s. Tap Play.";
    }

    [RelayCommand]
    async Task Play()
    {
        if (lastRecording == null)
            return;

        try
        {
            // Playback runs in a Playback session and follows the current output route
            // (built-in / Bluetooth / wired).
            await audio.Player.PlayAsync(new MemoryStream(lastRecording));
        }
        catch (Exception ex)
        {
            RecordStatus = $"Playback error: {ex.Message}";
            return;
        }

        RefreshRoutes();   // CurrentOutput now reflects where the clip actually played
        RecordStatus = $"Played to {CurrentOutputText}.";
    }

    // Wrap raw capture PCM (16 kHz / 16-bit / mono — the IAudioSource contract) in a WAV header so
    // IAudioPlayer can play it.
    static byte[] WavFromPcm16(byte[] pcm, int sampleRate = 16000, short channels = 1)
    {
        const short bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var buffer = new byte[44 + pcm.Length];
        var w = new BinaryWriter(new MemoryStream(buffer));
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);                                   // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)(channels * bitsPerSample / 8));      // block align
        w.Write(bitsPerSample);
        w.Write("data"u8.ToArray());
        w.Write(pcm.Length);
        w.Write(pcm);
        return buffer;
    }

    void OnLevel(object? sender, double value)
        => MainThread.BeginInvokeOnMainThread(() => Level = value);

    void OnDevicesChanged(object? sender, EventArgs e)
        => MainThread.BeginInvokeOnMainThread(RefreshRoutes);
}
