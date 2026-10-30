using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using MauiSample.Services;
using Shiny.AiConversation;
using Shiny.AiConversation.Voice;
using Shiny.Audio;
using Shiny.Speech;

namespace MauiSample.Features.Settings;

[ShellMap<SettingsPage>("settings")]
public partial class SettingsViewModel(
    ConversationHost host,
    IDialogs dialogs,
    IServiceProvider services
) : ObservableObject, IPageLifecycleAware
{
    readonly CloudProviderCredentials cloud = CloudProviderCredentials.Detect(services);
    CancellationTokenSource? eventsCts;

    // API-key editor — only surfaces when a 3rd-party cloud provider (Azure/ElevenLabs/OpenAI/Typecast)
    // is registered. Editing writes to the mutable provider config, taking effect on the next request.
    public bool HasCloudProvider => this.cloud.IsActive;
    public string CloudProviderName => this.cloud.ProviderName ?? "Cloud Provider";
    public IReadOnlyList<CredentialField> CredentialFields => this.cloud.Fields;

    public string[] AcknowledgementOptions => Enum.GetNames<AiAcknowledgement>();
    public string[] InterruptionOptions => Enum.GetNames<InterruptionMode>();

    public string SelectedAcknowledgementName
    {
        get => host.VoiceOptions.Acknowledgement.ToString();
        set
        {
            if (Enum.TryParse<AiAcknowledgement>(value, out var parsed) && parsed != host.VoiceOptions.Acknowledgement)
            {
                _ = host.UpdateVoice(o => o with { Acknowledgement = parsed });
                OnPropertyChanged();
            }
        }
    }

    public string SelectedInterruptionName
    {
        get => host.VoiceOptions.Interruption.ToString();
        set
        {
            if (Enum.TryParse<InterruptionMode>(value, out var parsed) && parsed != host.VoiceOptions.Interruption)
            {
                _ = host.UpdateVoice(o => o with { Interruption = parsed });
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    string wakeWordText = "Hey Copilot";

    [ObservableProperty]
    string? contextText;

    public bool IsWakeWordActive => host.VoiceOptions.WakeWord != null && host.Voice.Session != null;
    public bool IsNotWakeWordActive => !this.IsWakeWordActive;
    public string WakeWordButtonText => this.IsWakeWordActive ? "Stop Wake Word" : "Start Wake Word";
    public string CurrentStatus => host.Voice.State.ToString();

    public ObservableCollection<string> QuietWords { get; } = [];

    public async void OnAppearing()
    {
        QuietWords.Clear();
        foreach (var word in host.VoiceOptions.QuietWords)
            QuietWords.Add(word);

        // Re-sync the editable key fields with the current config values.
        foreach (var field in this.cloud.Fields)
            field.Reload();

        RefreshAll();
        this.eventsCts = new CancellationTokenSource();
        var ct = this.eventsCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in host.Voice.GetEvents(ct))
                    MainThread.BeginInvokeOnMainThread(RefreshAll);
            }
            catch (OperationCanceledException) { }
        }, ct);

        var session = await host.GetSession();
        this.ShowContext(session);
    }

    public void OnDisappearing()
    {
        this.eventsCts?.Cancel();
        this.eventsCts = null;
    }

    void ShowContext(IAiSession session)
    {
        var c = session.Context;
        var usage = session.Usage;
        this.ContextText = c.MaxTokens is { } max
            ? $"Context: {c.CurrentTokens:N0} of {max:N0} tokens ({c.PercentUsed:P0}, {c.Pressure}). Billed so far: {usage.TotalTokens:N0} tokens in {usage.RequestCount} requests."
            : $"Context: ~{c.CurrentTokens:N0} tokens{(c.IsEstimate ? " (estimated)" : "")}. Billed so far: {usage.TotalTokens:N0} tokens in {usage.RequestCount} requests.";
    }

    void RefreshAll()
    {
        OnPropertyChanged(nameof(IsWakeWordActive));
        OnPropertyChanged(nameof(IsNotWakeWordActive));
        OnPropertyChanged(nameof(WakeWordButtonText));
        OnPropertyChanged(nameof(CurrentStatus));
        OnPropertyChanged(nameof(SelectedAcknowledgementName));
        OnPropertyChanged(nameof(SelectedInterruptionName));
    }

    Task SyncQuietWords() => host.UpdateVoice(o => o with { QuietWords = QuietWords.ToList() });

    [RelayCommand]
    async Task AddQuietWord()
    {
        var word = await dialogs.Prompt("Add Quiet Word", "Enter a word or phrase:");
        if (!String.IsNullOrWhiteSpace(word))
        {
            QuietWords.Add(word.Trim());
            await SyncQuietWords();
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
                await SyncQuietWords();
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
            await SyncQuietWords();
        }
    }

    [RelayCommand]
    async Task ToggleWakeWord()
    {
        try
        {
            if (this.IsWakeWordActive)
            {
                await host.UpdateVoice(o => o with { WakeWord = null });
            }
            else
            {
                if (String.IsNullOrWhiteSpace(this.WakeWordText))
                {
                    await dialogs.Alert("Error", "Please enter a wake word.");
                    return;
                }
                var access = await host.Voice.RequestAccess();
                if (access != AccessState.Available)
                {
                    await dialogs.Alert("Error", $"Speech recognition is {access}.");
                    return;
                }
                await host.UpdateVoice(o => o with { WakeWord = this.WakeWordText.Trim() });
                await host.EnsureVoice();
            }
            RefreshAll();
        }
        catch (Exception ex)
        {
            await dialogs.Alert("Error", ex.Message);
        }
    }

    [RelayCommand]
    async Task NewConversation()
    {
        var session = await host.NewConversation();
        this.ShowContext(session);
        await dialogs.Alert("New Conversation", "Started a fresh conversation.");
    }

    [RelayCommand]
    async Task Compact()
    {
        var session = await host.GetSession();
        try
        {
            await session.Compact();
            this.ShowContext(session);
            await dialogs.Alert("Compacted", "Older messages were folded into a summary.");
        }
        catch (Exception ex)
        {
            await dialogs.Alert("Error", ex.Message);
        }
    }

    [RelayCommand]
    Task OpenMemory() => Shell.Current.GoToAsync("memory");

    [RelayCommand]
    async Task SaveKeys()
    {
        foreach (var field in this.cloud.Fields)
            field.Commit();

        await dialogs.Alert(
            "Saved",
            $"{this.CloudProviderName} credentials saved. They take effect on the next speech request and persist across restarts.");
    }

    [RelayCommand]
    async Task ClearHistory()
    {
        try
        {
            if (!await dialogs.Confirm("Delete every conversation?", "This can't be undone."))
                return;

            await host.DeleteAll();
            await dialogs.Alert("Done", "All conversations deleted.");
        }
        catch (Exception ex)
        {
            await dialogs.Alert("Error", ex.Message);
        }
    }
}
