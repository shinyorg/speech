using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Shiny.Speech;

namespace MauiSample;

public class MainViewModel : INotifyPropertyChanged
{
    readonly ISpeechToTextService speechToText;
    readonly ITextToSpeechService textToSpeech;
    CancellationTokenSource? listenCts;

    public MainViewModel(ISpeechToTextService speechToText, ITextToSpeechService textToSpeech)
    {
        this.speechToText = speechToText;
        this.textToSpeech = textToSpeech;

        SpeakCommand = new Command(async () => await SpeakAsync());
        StopSpeakingCommand = new Command(async () => await textToSpeech.StopAsync());
        ListenCommand = new Command(async () => await ListenAsync());
    }

    string textToSpeak = "Hello! I am the Shiny Speech library.";
    public string TextToSpeak
    {
        get => textToSpeak;
        set { textToSpeak = value; OnPropertyChanged(); }
    }

    string? recognizedText;
    public string? RecognizedText
    {
        get => recognizedText;
        set { recognizedText = value; OnPropertyChanged(); }
    }

    string? statusText;
    public string? StatusText
    {
        get => statusText;
        set { statusText = value; OnPropertyChanged(); }
    }

    public ICommand SpeakCommand { get; }
    public ICommand StopSpeakingCommand { get; }
    public ICommand ListenCommand { get; }

    async Task SpeakAsync()
    {
        if (string.IsNullOrWhiteSpace(TextToSpeak))
            return;

        StatusText = "Speaking...";
        await textToSpeech.SpeakAsync(TextToSpeak);
        StatusText = "Done speaking.";
    }

    async Task ListenAsync()
    {
        var access = await speechToText.RequestAccess();
        if (access != AccessState.Available)
        {
            StatusText = $"Speech recognition access: {access}";
            return;
        }

        listenCts?.Cancel();
        listenCts = new CancellationTokenSource();

        StatusText = "Listening...";
        RecognizedText = "";

        try
        {
            await foreach (var result in speechToText.ContinuousRecognize(cancellationToken: listenCts.Token))
            {
                RecognizedText = result.Text;
                if (result.IsFinal)
                {
                    StatusText = $"Final (confidence: {result.Confidence:P0})";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }

        if (string.IsNullOrEmpty(StatusText) || StatusText == "Listening...")
            StatusText = "Done listening.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
