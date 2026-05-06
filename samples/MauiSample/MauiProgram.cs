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
            .UseShinyShell(cfg =>
            {
                cfg.Add<SpeechToTextPage, SpeechToTextViewModel>();
                cfg.Add<TextToSpeechPage, TextToSpeechViewModel>();
            })
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Register native platform speech services
        builder.Services.AddSpeechServices();

        // To use Azure cloud speech instead:
        // builder.Services.AddAzureSpeech("your-key", "your-region");

        // To use ElevenLabs for TTS:
        // builder.Services.AddElevenLabsTextToSpeech("your-api-key");

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
