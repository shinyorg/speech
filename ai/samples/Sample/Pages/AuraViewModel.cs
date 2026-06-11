using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.AiConversation;
using Shiny.Speech;

namespace Sample.Pages;

public partial class AuraViewModel(
    IAiConversationService aiService,
    ITextToSpeechService tts,
    IDialogs dialogs
) : ObservableObject, IPageLifecycleAware
{
    CancellationTokenSource? listenCts;

    public string StatusText => aiService.Status.ToString();
    public AiState CurrentState => aiService.Status;

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

    /// <summary>0..1 audio output level fed from the TTS service while it's speaking.</summary>
    [ObservableProperty]
    double audioLevel;

    /// <summary>Pretty-formatted token burn for the most recent AI response, or null when none yet.</summary>
    [ObservableProperty]
    string? lastTokenBurn;

    [RelayCommand]
    void DismissResponse() => this.IsResponseVisible = false;

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task AuraTapped()
    {
        if (aiService.Status == AiState.Idle)
        {
            var access = await aiService.RequestAccess();
            if (access != AccessState.Available)
                return;

            this.listenCts = new CancellationTokenSource();
            try
            {
                await aiService.ListenAndTalk(this.listenCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
            finally
            {
                this.listenCts?.Dispose();
                this.listenCts = null;
            }
        }
        else if (aiService.WakeWord == null)
        {
            this.listenCts?.Cancel();
        }
    }

    public void OnAppearing()
    {
        aiService.StatusChanged += this.OnStatusChanged;
        aiService.AiResponded += this.OnAiResponded;
        aiService.SpeechResultReceived += this.OnSpeechResult;
        aiService.SpeechOccurred += this.OnSpeechOccurred;
        aiService.ErrorOccurred += this.OnErrorOccurred;
        tts.AudioLevelChanged += this.OnAudioLevelChanged;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CurrentState));
    }

    public void OnDisappearing()
    {
        aiService.StatusChanged -= this.OnStatusChanged;
        aiService.AiResponded -= this.OnAiResponded;
        aiService.SpeechResultReceived -= this.OnSpeechResult;
        aiService.SpeechOccurred -= this.OnSpeechOccurred;
        aiService.ErrorOccurred -= this.OnErrorOccurred;
        tts.AudioLevelChanged -= this.OnAudioLevelChanged;
    }

    async void OnErrorOccurred(Exception ex)
        => await dialogs.Alert("Error", ex.Message);

    void OnSpeechResult(SpeechRecognitionResult result)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            this.LivePartialText = result.IsFinal ? null : result.Text;
        });
    }

    void OnAiResponded(AiResponse response)
    {
        var usage = response.Response.Usage;
        var burn = ChatViewModel.FormatTokenFooter(
            usage?.InputTokenCount,
            usage?.OutputTokenCount,
            usage?.TotalTokenCount
        );

        if (response.WasReadAloud)
        {
            if (burn is not null)
                MainThread.BeginInvokeOnMainThread(() => this.LastTokenBurn = burn);
            return;
        }

        if (response.Response.Text is { } text)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                this.LastResponseText = text;
                this.IsResponseVisible = true;
                if (burn is not null)
                    this.LastTokenBurn = burn;
            });
        }
    }

    void OnStatusChanged(AiState state)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CurrentState));
            if (state != AiState.Responding)
                this.AudioLevel = 0;
            if (state == AiState.Listening)
            {
                this.HeardText = null;
                this.SpokenText = null;
            }
        });
    }

    void OnSpeechOccurred(ConversationSpeech speech)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (speech.Source)
            {
                case ConversationSpeechSource.Heard:
                    this.HeardText = speech.Text;
                    break;
                case ConversationSpeechSource.Spoken:
                    this.SpokenText = speech.Text;
                    break;
            }
        });
    }

    void OnAudioLevelChanged(object? sender, double level)
    {
        MainThread.BeginInvokeOnMainThread(() => this.AudioLevel = level);
    }
}
