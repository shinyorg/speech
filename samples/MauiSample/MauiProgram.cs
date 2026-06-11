using MauiSample.Pages;
using MauiSample.Services;
using Microsoft.Extensions.Logging;
using Shiny;
using Shiny.AiConversation;

namespace MauiSample;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseShinyControls()
            .UseShinyShell(cfg =>
            {
                cfg.Add<SpeechToTextPage, SpeechToTextViewModel>();
                cfg.Add<TextToSpeechPage, TextToSpeechViewModel>();
                cfg.Add<ChatPage, ChatViewModel>("chat");
                cfg.Add<SettingsPage, SettingsViewModel>("settings");
                cfg.Add<AuraPage, AuraViewModel>("aura");
            })
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Register native platform speech services
        builder.Services.AddSpeechServices();

        // AI Conversation
        builder.Services.AddSingleton<IContextProvider, SampleContextProvider>();
        builder.Services.AddShinyAiConversation(opts =>
        {
            opts.AddGithubCopilotChatClient();

            var dbPath = Path.Combine(FileSystem.AppDataDirectory, "sample_ai.db");
            opts.SetSqliteDocDbMessageStore(dbPath);
        });

        // To use Azure cloud speech instead:
        // builder.Services.AddAzureSpeech("your-key", "your-region");

        // To use ElevenLabs instead:
        // builder.Services.AddElevenLabsSpeech("");

        // builder.Services.AddElevenLabsTextToSpeech("your-api-key");

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
