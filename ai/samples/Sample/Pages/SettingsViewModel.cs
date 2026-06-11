using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.AiConversation;
using Shiny.Speech;

namespace Sample.Pages;

public partial class SettingsViewModel(IAiConversationService aiService, ContextProvider contextProvider, IDialogs dialogs)
    : ObservableObject, IPageLifecycleAware
{
    public string[] AcknowledgementOptions => Enum.GetNames<AiAcknowledgement>();

    public string SelectedAcknowledgementName
    {
        get => aiService.Acknowledgement.ToString();
        set
        {
            if (Enum.TryParse<AiAcknowledgement>(value, out var parsed))
            {
                aiService.Acknowledgement = parsed;
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    string wakeWordText = "Hey Copilot";

    public bool IsWakeWordActive => aiService.WakeWord != null;
    public bool IsNotWakeWordActive => !this.IsWakeWordActive;
    public string WakeWordButtonText => this.IsWakeWordActive ? "Stop Wake Word" : "Start Wake Word";
    public string CurrentStatus => aiService.Status.ToString();

    public bool InterruptionEnabled
    {
        get => aiService.InterruptionEnabled;
        set
        {
            aiService.InterruptionEnabled = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<string> QuietWords { get; } = [];

    public void OnAppearing()
    {
        aiService.StatusChanged += this.OnStatusChanged;

        QuietWords.Clear();
        foreach (var word in contextProvider.GetQuietWords())
            QuietWords.Add(word);

        RefreshAll();
    }

    public void OnDisappearing()
    {
        aiService.StatusChanged -= this.OnStatusChanged;
    }

    void OnStatusChanged(AiState state)
    {
        MainThread.BeginInvokeOnMainThread(RefreshAll);
    }

    void RefreshAll()
    {
        OnPropertyChanged(nameof(IsWakeWordActive));
        OnPropertyChanged(nameof(IsNotWakeWordActive));
        OnPropertyChanged(nameof(WakeWordButtonText));
        OnPropertyChanged(nameof(CurrentStatus));
        OnPropertyChanged(nameof(SelectedAcknowledgementName));
        OnPropertyChanged(nameof(InterruptionEnabled));
    }

    void SyncQuietWords()
    {
        contextProvider.SetQuietWords(QuietWords);
    }

    [RelayCommand]
    async Task AddQuietWord()
    {
        var word = await dialogs.Prompt("Add Quiet Word", "Enter a word or phrase:");
        if (!String.IsNullOrWhiteSpace(word))
        {
            QuietWords.Add(word.Trim());
            SyncQuietWords();
        }
    }

    [RelayCommand]
    async Task EditQuietWord(string word)
    {
        var updated = await dialogs.Prompt("Edit Quiet Word", "Update the word or phrase:", word);
        if (updated != null && !String.IsNullOrWhiteSpace(updated))
        {
            var index = QuietWords.IndexOf(word);
            if (index >= 0)
            {
                QuietWords[index] = updated.Trim();
                SyncQuietWords();
            }
        }
    }

    [RelayCommand]
    async Task RemoveQuietWord(string word)
    {
        var confirm = await dialogs.Confirm($"Remove \"{word}\"?", "Are you sure?");
        if (confirm)
        {
            QuietWords.Remove(word);
            SyncQuietWords();
        }
    }

    [RelayCommand]
    async Task ToggleWakeWord()
    {
        try
        {
            if (this.IsWakeWordActive)
            {
                await aiService.StopWakeWord();
            }
            else
            {
                if (String.IsNullOrWhiteSpace(this.WakeWordText))
                {
                    await dialogs.Alert("Error", "Please enter a wake word.");
                    return;
                }
                var access = await aiService.RequestAccess();
                if (access != AccessState.Available)
                {
                    await dialogs.Alert("Error", "Speech recognition is not available on this device.");
                    return;
                }
                await aiService.StartWakeWord(this.WakeWordText);
            }
        }
        catch (Exception ex)
        {
            await dialogs.Alert("Error", ex.Message);
        }
    }

    [RelayCommand]
    async Task ClearHistory()
    {
        try
        {
            await aiService.ClearChatHistory();
            await dialogs.Alert("Done", "Chat history cleared.");
        }
        catch (Exception ex)
        {
            await dialogs.Alert("Error", ex.Message);
        }
    }
}
