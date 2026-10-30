using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MauiSample.Services;
using Shiny;
using Shiny.AiConversation;
using Shiny.AiConversation.Voice;

namespace MauiSample.Features.Aura;

[ShellMap<AuraPage>("aura")]
public partial class AuraViewModel(ConversationHost host, IDialogs dialogs) : ObservableObject, IPageLifecycleAware
{
    CancellationTokenSource? eventsCts;

    public VoiceState CurrentState => host.Voice.State;
    public string StatusText => host.Voice.State.ToString();

    [ObservableProperty]
    string? lastResponseText;

    [ObservableProperty]
    bool isResponseVisible;

    [ObservableProperty]
    string? livePartialText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeardText))]
    string? heardText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpokenText))]
    string? spokenText;

    public bool HasHeardText => !String.IsNullOrWhiteSpace(this.HeardText);
    public bool HasSpokenText => !String.IsNullOrWhiteSpace(this.SpokenText);

    /// <summary>0..1 output level while speaking.</summary>
    [ObservableProperty]
    double audioLevel;

    /// <summary>Token burn of the most recent reply, or null when none yet.</summary>
    [ObservableProperty]
    string? lastTokenBurn;

    /// <summary>End of your utterance to the first audio of the reply.</summary>
    [ObservableProperty]
    string? latencyText;

    [RelayCommand]
    void DismissResponse() => this.IsResponseVisible = false;


    /// <summary>What interrupted the last reply and how much of it had been heard.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInterruptionText))]
    string? interruptionText;

    public bool HasInterruptionText => !String.IsNullOrWhiteSpace(this.InterruptionText);

    public string InterruptionModeText => host.VoiceOptions.Interruption switch
    {
        InterruptionMode.QuietWords => "INTERRUPT: QUIET WORDS",
        InterruptionMode.Full => "INTERRUPT: FULL",
        _ => "INTERRUPT: OFF"
    };


    /// <summary>QuietWords → Full → Off: re-attaches the voice conversation with the new mode.</summary>
    [RelayCommand]
    async Task CycleInterruption()
    {
        var next = host.VoiceOptions.Interruption switch
        {
            InterruptionMode.QuietWords => InterruptionMode.Full,
            InterruptionMode.Full => InterruptionMode.Off,
            _ => InterruptionMode.QuietWords
        };
        await host.UpdateVoice(o => o with { Interruption = next });
        this.OnPropertyChanged(nameof(this.InterruptionModeText));
        this.InterruptionText = next switch
        {
            InterruptionMode.QuietWords => $"Say one of: {String.Join(", ", host.VoiceOptions.QuietWords)} - or answer its question (\"yes\", \"the second one\") before it finishes",
            InterruptionMode.Full => "Just talk over K.I.T.T., or answer its question early (works best with headphones)",
            _ => "The mic is muted while K.I.T.T. talks; tap the aura or STOP to interrupt"
        };
    }


    [RelayCommand]
    Task StopSpeaking() => host.Voice.StopSpeaking();


    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task AuraTapped()
    {
        try
        {
            var access = await host.Voice.RequestAccess();
            if (access != AccessState.Available)
            {
                await dialogs.Alert("Microphone", $"Speech recognition is {access}.");
                return;
            }

            await host.EnsureVoice();

            // tapping while it talks stops the reply and listens; tapping while it listens does nothing extra
            if (host.Voice.State is VoiceState.Off or VoiceState.WaitingForWakeWord or VoiceState.Speaking or VoiceState.Thinking)
                await host.Voice.PushToTalk();
        }
        catch (Exception ex)
        {
            await dialogs.Alert("Error", ex.Message);
        }
    }


    public async void OnAppearing()
    {
        this.eventsCts = new CancellationTokenSource();
        var ct = this.eventsCts.Token;
        this.Refresh();

        var session = await host.GetSession();
        _ = Task.Run(() => this.WatchVoice(ct), ct);
        _ = Task.Run(() => this.WatchSession(session, ct), ct);
    }


    public void OnDisappearing()
    {
        this.eventsCts?.Cancel();
        this.eventsCts = null;
    }


    async Task WatchVoice(CancellationToken ct)
    {
        try
        {
            await foreach (var e in host.Voice.GetEvents(ct))
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    switch (e)
                    {
                        case VoiceStateChanged s:
                            this.Refresh();
                            if (s.State != VoiceState.Speaking)
                                this.AudioLevel = 0;

                            if (s.State is VoiceState.Listening or VoiceState.FollowUp)
                            {
                                this.HeardText = null;
                                this.SpokenText = null;
                                this.LivePartialText = null;
                            }
                            break;

                        case InterimTranscript t:
                            this.LivePartialText = t.Text;
                            break;

                        case FinalTranscript t:
                            this.LivePartialText = null;
                            this.HeardText = t.Text;
                            break;

                        case SpeakingSentence s:
                            this.SpokenText = String.IsNullOrEmpty(this.SpokenText) ? s.Text : this.SpokenText + " " + s.Text;
                            break;

                        case AudioLevel l:
                            this.AudioLevel = l.Output;
                            break;

                        case SpeechInterrupted i:
                            var heard = String.IsNullOrWhiteSpace(i.SpokenText) ? "nothing yet" : $"\"{i.SpokenText}\"";
                            this.InterruptionText = i.Reason switch
                            {
                                InterruptionReason.Answer => $"Answered \"{i.HeardText}\" after hearing {heard}",
                                InterruptionReason.QuietWord => $"Stopped by \"{i.HeardText}\" after hearing {heard}",
                                InterruptionReason.NewSpeech => $"Talked over with \"{i.HeardText}\" after hearing {heard}",
                                InterruptionReason.PushToTalk => $"Tapped to talk after hearing {heard}",
                                _ => $"Stopped after hearing {heard}"
                            };
                            break;

                        case ResponseLatency l:
                            this.LatencyText = $"{l.Latency.TotalMilliseconds:N0} ms to first audio";
                            break;

                        case VoiceError err:
                            _ = dialogs.Alert("Voice error", err.Error.Message);
                            break;
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
    }


    async Task WatchSession(IAiSession session, CancellationToken ct)
    {
        try
        {
            await foreach (var e in session.GetEvents(ct))
            {
                if (e is not MessageUpdated { Message: { Role: MessageRole.Assistant, Status: MessageStatus.Complete } reply })
                    continue;

                var burn = AiChatTokens.FormatTokenFooter(reply.Usage?.InputTokens, reply.Usage?.OutputTokens, reply.Usage?.TotalTokens);
                var spoken = host.VoiceOptions.Acknowledgement >= AiAcknowledgement.LessWordy;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (burn != null)
                        this.LastTokenBurn = burn;

                    // without text-to-speech the reply is shown instead
                    if (!spoken && reply.Text is { } text)
                    {
                        this.LastResponseText = text;
                        this.IsResponseVisible = true;
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
    }


    void Refresh()
    {
        this.OnPropertyChanged(nameof(this.StatusText));
        this.OnPropertyChanged(nameof(this.CurrentState));
    }
}
