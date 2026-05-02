namespace MauiSample;

public partial class TextToSpeechPage : ContentPage
{
    public TextToSpeechPage(TextToSpeechViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is TextToSpeechViewModel vm)
            vm.LoadVoicesCommand.Execute(null);
    }
}
