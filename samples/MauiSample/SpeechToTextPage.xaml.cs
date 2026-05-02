namespace MauiSample;

public partial class SpeechToTextPage : ContentPage
{
    public SpeechToTextPage(SpeechToTextViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
