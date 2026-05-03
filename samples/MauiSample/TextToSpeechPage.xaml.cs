namespace MauiSample;

public partial class TextToSpeechPage : ContentPage
{
    public TextToSpeechPage(TextToSpeechViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
