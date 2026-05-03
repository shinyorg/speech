using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny.Speech;

namespace MauiSample;

public partial class SpeechToTextViewModel : ObservableObject
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
    }

    public List<CultureInfo> AvailableLocales { get; }

    [ObservableProperty]
    CultureInfo selectedLocale = null!;

    [ObservableProperty]
    bool preferOnDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SilenceTimeoutText))]
    double silenceTimeoutSeconds = 3;

    public string SilenceTimeoutText => $"Silence Timeout: {SilenceTimeoutSeconds:0}s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContinuousButtonColor))]
    [NotifyPropertyChangedFor(nameof(UntilSilenceButtonColor))]
    bool isContinuousMode = true;

    public Color ContinuousButtonColor =>
        IsContinuousMode
            ? Color.FromArgb("#9A81EA")
            : Color.FromArgb("#6E6E6E");

    public Color UntilSilenceButtonColor =>
        !IsContinuousMode
            ? Color.FromArgb("#9A81EA")
            : Color.FromArgb("#6E6E6E");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListenButtonText))]
    bool isListening;

    public string ListenButtonText => IsListening ? "Stop Listening" : "Start Listening";

    [ObservableProperty]
    string statusText = "Ready";

    [ObservableProperty]
    string? recognizedText;

    [ObservableProperty]
    string? confidenceText;

    [ObservableProperty]
    bool hasConfidence;

    [RelayCommand]
    void SetContinuousMode() => IsContinuousMode = true;

    [RelayCommand]
    void SetUntilSilenceMode() => IsContinuousMode = false;

    [RelayCommand]
    void Clear()
    {
        RecognizedText = "";
        ConfidenceText = "";
        HasConfidence = false;
        StatusText = "Ready";
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
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

        var accumulated = "";
        var lastFinal = "";

        await foreach (var result in stt.ContinuousRecognize(options, ct))
        {
            if (result.IsFinal)
            {
                // Append final segment to accumulated text
                if (accumulated.Length > 0)
                    accumulated += " ";
                accumulated += result.Text;
                lastFinal = accumulated;
                RecognizedText = accumulated;
            }
            else
            {
                // Show accumulated finals + current partial
                RecognizedText = accumulated.Length > 0
                    ? accumulated + " " + result.Text
                    : result.Text;
            }

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
}
