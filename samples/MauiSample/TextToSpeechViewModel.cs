using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.Speech;

namespace MauiSample;

public partial class TextToSpeechViewModel : ObservableObject, IPageLifecycleAware
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
    }

    public List<CultureInfo> AvailableLocales { get; }

    public ObservableCollection<VoiceInfo> Voices { get; } = new();

    [ObservableProperty]
    CultureInfo? selectedLocale;

    [ObservableProperty]
    VoiceInfo? selectedVoice;

    [ObservableProperty]
    string textToSpeak = "Hello! I am the Shiny Speech library. How does this voice sound?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateText))]
    double speechRate = 1.0;

    public string RateText => $"Rate: {SpeechRate:F1}x";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PitchText))]
    double pitch = 1.0;

    public string PitchText => $"Pitch: {Pitch:F1}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    double volume = 1.0;

    public string VolumeText => $"Volume: {Volume:P0}";

    [ObservableProperty]
    string statusText = "Ready";

    public string VoiceCountText => $"Voice ({Voices.Count} available)";

    partial void OnSelectedLocaleChanged(CultureInfo? value)
        => LoadVoicesCommand.Execute(null);

    public void OnAppearing() => LoadVoicesCommand.Execute(null);
    public void OnDisappearing() { }

    [RelayCommand]
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

    [RelayCommand]
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

    [RelayCommand]
    async Task StopAsync() => await tts.StopAsync();
}
