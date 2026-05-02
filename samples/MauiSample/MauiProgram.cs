using Microsoft.Extensions.Logging;
using Shiny;

namespace MauiSample;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Register native platform speech services
        builder.Services.AddSpeechServices();

        // To use Azure cloud speech instead:
        // builder.Services.AddAudioSource();
        // builder.Services.AddAudioPlayer();
        // builder.Services.AddAzureSpeech("your-key", "your-region");

        // To use ElevenLabs for TTS:
        // builder.Services.AddAudioPlayer();
        // builder.Services.AddElevenLabsTextToSpeech("your-api-key");

        builder.Services.AddTransient<SpeechToTextPage>();
        builder.Services.AddTransient<SpeechToTextViewModel>();
        builder.Services.AddTransient<TextToSpeechPage>();
        builder.Services.AddTransient<TextToSpeechViewModel>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
