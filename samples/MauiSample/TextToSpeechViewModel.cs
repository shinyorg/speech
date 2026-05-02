using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Shiny.Speech;

namespace MauiSample;

public class TextToSpeechViewModel : INotifyPropertyChanged
{
    readonly ITextToSpeechService tts;

    public TextToSpeechViewModel(ITextToSpeechService tts)
    {
        this.tts = tts;

        var cultures = CultureInfo
            .GetCultures(CultureTypes.SpecificCultures)
            .OrderBy(c => c.DisplayName)
            .ToList();
        cultures.Insert(0, CultureInfo.InvariantCulture); // "All" option
        AvailableLocales = cultures;

        SpeakCommand = new Command(async () => await SpeakAsync());
        StopCommand = new Command(async () => await tts.StopAsync());
        LoadVoicesCommand = new Command(async () => await LoadVoicesAsync());
    }

    public List<CultureInfo> AvailableLocales { get; }

    CultureInfo? selectedLocale;
    public CultureInfo? SelectedLocale
    {
        get => selectedLocale;
        set
        {
            selectedLocale = value;
            OnPropertyChanged();
            LoadVoicesCommand.Execute(null);
        }
    }

    public ObservableCollection<VoiceInfo> Voices { get; } = new();

    VoiceInfo? selectedVoice;
    public VoiceInfo? SelectedVoice
    {
        get => selectedVoice;
        set { selectedVoice = value; OnPropertyChanged(); }
    }

    string textToSpeak = "Hello! I am the Shiny Speech library. How does this voice sound?";
    public string TextToSpeak
    {
        get => textToSpeak;
        set { textToSpeak = value; OnPropertyChanged(); }
    }

    double speechRate = 1.0;
    public double SpeechRate
    {
        get => speechRate;
        set { speechRate = value; OnPropertyChanged(); OnPropertyChanged(nameof(RateText)); }
    }
    public string RateText => $"Rate: {SpeechRate:F1}x";

    double pitch = 1.0;
    public double Pitch
    {
        get => pitch;
        set { pitch = value; OnPropertyChanged(); OnPropertyChanged(nameof(PitchText)); }
    }
    public string PitchText => $"Pitch: {Pitch:F1}";

    double volume = 1.0;
    public double Volume
    {
        get => volume;
        set { volume = value; OnPropertyChanged(); OnPropertyChanged(nameof(VolumeText)); }
    }
    public string VolumeText => $"Volume: {Volume:P0}";

    string statusText = "Ready";
    public string StatusText
    {
        get => statusText;
        set { statusText = value; OnPropertyChanged(); }
    }

    public string VoiceCountText => $"Voice ({Voices.Count} available)";

    public ICommand SpeakCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand LoadVoicesCommand { get; }

    async Task LoadVoicesAsync()
    {
        try
        {
            var culture = SelectedLocale == CultureInfo.InvariantCulture
                ? null
                : SelectedLocale;

            var voices = await tts.GetVoicesAsync(culture);

            Voices.Clear();
            foreach (var v in voices.OrderBy(v => v.Name))
                Voices.Add(v);

            OnPropertyChanged(nameof(VoiceCountText));
            SelectedVoice = Voices.FirstOrDefault();
            StatusText = $"Loaded {Voices.Count} voices";
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading voices: {ex.Message}";
        }
    }

    async Task SpeakAsync()
    {
        if (string.IsNullOrWhiteSpace(TextToSpeak))
            return;

        StatusText = "Speaking...";
        try
        {
            var options = new TextToSpeechOptions
            {
                Voice = SelectedVoice,
                SpeechRate = (float)SpeechRate,
                Pitch = (float)Pitch,
                Volume = (float)Volume
            };

            if (SelectedLocale != null && SelectedLocale != CultureInfo.InvariantCulture)
                options = options with { Culture = SelectedLocale };

            await tts.SpeakAsync(TextToSpeak, options);
            StatusText = "Done";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
