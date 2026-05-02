using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Shiny.Speech;

namespace MauiSample;

public class SpeechToTextViewModel : INotifyPropertyChanged
{
    readonly ISpeechToTextService stt;
    CancellationTokenSource? listenCts;

    public SpeechToTextViewModel(ISpeechToTextService stt)
    {
        this.stt = stt;

        AvailableLocales = CultureInfo
            .GetCultures(CultureTypes.SpecificCultures)
            .OrderBy(c => c.DisplayName)
            .ToList();

        SelectedLocale = AvailableLocales
            .FirstOrDefault(c => c.Name == CultureInfo.CurrentCulture.Name)
            ?? AvailableLocales.First();

        ToggleListenCommand = new Command(async () => await ToggleListenAsync());
        ClearCommand = new Command(() =>
        {
            RecognizedText = "";
            ConfidenceText = "";
            HasConfidence = false;
            StatusText = "Ready";
        });
        SetContinuousModeCommand = new Command(() => IsContinuousMode = true);
        SetUntilSilenceModeCommand = new Command(() => IsContinuousMode = false);
    }

    public List<CultureInfo> AvailableLocales { get; }

    CultureInfo selectedLocale = null!;
    public CultureInfo SelectedLocale
    {
        get => selectedLocale;
        set { selectedLocale = value; OnPropertyChanged(); }
    }

    bool preferOnDevice;
    public bool PreferOnDevice
    {
        get => preferOnDevice;
        set { preferOnDevice = value; OnPropertyChanged(); }
    }

    double silenceTimeoutSeconds = 3;
    public double SilenceTimeoutSeconds
    {
        get => silenceTimeoutSeconds;
        set
        {
            silenceTimeoutSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SilenceTimeoutText));
        }
    }

    public string SilenceTimeoutText => $"Silence Timeout: {SilenceTimeoutSeconds:0}s";

    bool isContinuousMode = true;
    public bool IsContinuousMode
    {
        get => isContinuousMode;
        set
        {
            isContinuousMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ContinuousButtonColor));
            OnPropertyChanged(nameof(UntilSilenceButtonColor));
        }
    }

    public Color ContinuousButtonColor =>
        IsContinuousMode
            ? Color.FromArgb("#9A81EA")
            : Color.FromArgb("#6E6E6E");

    public Color UntilSilenceButtonColor =>
        !IsContinuousMode
            ? Color.FromArgb("#9A81EA")
            : Color.FromArgb("#6E6E6E");

    bool isListening;
    public bool IsListening
    {
        get => isListening;
        set
        {
            isListening = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ListenButtonText));
        }
    }

    public string ListenButtonText => IsListening ? "Stop Listening" : "Start Listening";

    string statusText = "Ready";
    public string StatusText
    {
        get => statusText;
        set { statusText = value; OnPropertyChanged(); }
    }

    string? recognizedText;
    public string? RecognizedText
    {
        get => recognizedText;
        set { recognizedText = value; OnPropertyChanged(); }
    }

    string? confidenceText;
    public string? ConfidenceText
    {
        get => confidenceText;
        set { confidenceText = value; OnPropertyChanged(); }
    }

    bool hasConfidence;
    public bool HasConfidence
    {
        get => hasConfidence;
        set { hasConfidence = value; OnPropertyChanged(); }
    }

    public ICommand ToggleListenCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand SetContinuousModeCommand { get; }
    public ICommand SetUntilSilenceModeCommand { get; }

    async Task ToggleListenAsync()
    {
        if (IsListening)
        {
            listenCts?.Cancel();
            return;
        }

        var access = await stt.RequestAccess();
        if (access != AccessState.Available)
        {
            StatusText = $"Access: {access}";
            return;
        }

        listenCts?.Cancel();
        listenCts = new CancellationTokenSource();
        IsListening = true;
        RecognizedText = "";
        HasConfidence = false;

        try
        {
            if (IsContinuousMode)
                await ListenContinuousAsync(listenCts.Token);
            else
                await ListenUntilSilenceAsync(listenCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsListening = false;
            if (StatusText.StartsWith("Listening"))
                StatusText = "Done";
        }
    }

    async Task ListenContinuousAsync(CancellationToken ct)
    {
        StatusText = "Listening (continuous)...";

        var options = new SpeechRecognitionOptions
        {
            Culture = SelectedLocale,
            SilenceTimeout = TimeSpan.FromSeconds(SilenceTimeoutSeconds),
            PreferOnDevice = PreferOnDevice
        };

        await foreach (var result in stt.ContinuousRecognize(options, ct))
        {
            RecognizedText = result.Text;

            if (result.Confidence.HasValue)
            {
                HasConfidence = true;
                ConfidenceText = $"Confidence: {result.Confidence:P0}";
            }

            StatusText = result.IsFinal
                ? "Listening (continuous) — final segment received"
                : "Listening (continuous) — partial...";
        }

        StatusText = "Continuous listening ended";
    }

    async Task ListenUntilSilenceAsync(CancellationToken ct)
    {
        StatusText = "Listening (until silence)...";

        var options = new SpeechRecognitionOptions
        {
            Culture = SelectedLocale,
            SilenceTimeout = TimeSpan.FromSeconds(SilenceTimeoutSeconds),
            PreferOnDevice = PreferOnDevice
        };

        var result = await stt.ListenUntilSilence(options, ct);

        if (result != null)
        {
            RecognizedText = result;
            StatusText = "Silence detected — done";
        }
        else
        {
            StatusText = "No speech detected";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
