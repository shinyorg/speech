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
    [NotifyPropertyChangedFor(nameof(WakeWordButtonColor))]
    [NotifyPropertyChangedFor(nameof(KeywordButtonColor))]
    [NotifyPropertyChangedFor(nameof(IsWakeWordMode))]
    [NotifyPropertyChangedFor(nameof(IsKeywordMode))]
    string selectedMode = "Continuous";

    public bool IsWakeWordMode => SelectedMode == "WakeWord";
    public bool IsKeywordMode => SelectedMode == "Keyword";

    public Color ContinuousButtonColor =>
        SelectedMode == "Continuous"
            ? Color.FromArgb("#9A81EA")
            : Color.FromArgb("#6E6E6E");

    public Color UntilSilenceButtonColor =>
        SelectedMode == "UntilSilence"
            ? Color.FromArgb("#9A81EA")
            : Color.FromArgb("#6E6E6E");

    public Color WakeWordButtonColor =>
        SelectedMode == "WakeWord"
            ? Color.FromArgb("#9A81EA")
            : Color.FromArgb("#6E6E6E");

    public Color KeywordButtonColor =>
        SelectedMode == "Keyword"
            ? Color.FromArgb("#9A81EA")
            : Color.FromArgb("#6E6E6E");

    [ObservableProperty]
    string wakePhrase = "Hey Computer";

    [ObservableProperty]
    string keywordsText = "Yes, No, Maybe";

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
    void SetMode(string mode) => SelectedMode = mode;

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
            switch (SelectedMode)
            {
                case "Continuous":
                    await ListenContinuousAsync(listenCts.Token);
                    break;
                case "UntilSilence":
                    await ListenUntilSilenceAsync(listenCts.Token);
                    break;
                case "WakeWord":
                    await ListenWakeWordAsync(listenCts.Token);
                    break;
                case "Keyword":
                    await ListenKeywordAsync(listenCts.Token);
                    break;
            }
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

    async Task ListenWakeWordAsync(CancellationToken ct)
    {
        StatusText = $"Listening for wake word: \"{WakePhrase}\"...";

        var options = new SpeechRecognitionOptions
        {
            Culture = SelectedLocale,
            SilenceTimeout = TimeSpan.FromSeconds(SilenceTimeoutSeconds),
            PreferOnDevice = PreferOnDevice
        };

        var result = await stt.ListenWithWakeWord(WakePhrase, options, ct);

        if (result != null)
        {
            RecognizedText = result;
            StatusText = "Wake word detected — command captured";
        }
        else
        {
            StatusText = "No command detected";
        }
    }

    async Task ListenKeywordAsync(CancellationToken ct)
    {
        var keywords = KeywordsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        StatusText = $"Listening for keywords: {string.Join(", ", keywords)}...";

        var options = new SpeechRecognitionOptions
        {
            Culture = SelectedLocale,
            SilenceTimeout = TimeSpan.FromSeconds(SilenceTimeoutSeconds),
            PreferOnDevice = PreferOnDevice
        };

        var result = await stt.ListenForKeyword(keywords, options, ct);

        if (result != null)
        {
            RecognizedText = $"Keyword detected: {result}";
            StatusText = $"Matched keyword: \"{result}\"";
        }
        else
        {
            StatusText = "No keyword detected";
        }
    }
}
