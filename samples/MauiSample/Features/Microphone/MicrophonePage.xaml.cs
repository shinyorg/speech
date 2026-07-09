namespace MauiSample.Features.Microphone;

public partial class MicrophonePage : ContentPage
{
    public MicrophonePage() => InitializeComponent();

    // Never leave the mic open when the page is left — that's feedback and battery drain.
    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (BindingContext is MicrophoneViewModel vm)
            await vm.StopIfRunning();
    }
}
