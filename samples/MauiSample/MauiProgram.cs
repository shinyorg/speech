using MauiSample.Features.Settings;
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
            .UseShiny() // required: native speech/audio permission handling routes through Shiny.Core (AndroidPlatform)
            .UseShinyControls()
            .UseShinyShell(cfg => cfg.AddGeneratedMaps())
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // AI Conversation
        builder.Services.AddSingleton<IContextProvider, SampleContextProvider>();
        builder.Services.AddShinyAiConversation(opts =>
        {
            opts.AddGithubCopilotChatClient();

            var dbPath = Path.Combine(FileSystem.AppDataDirectory, "sample_ai.db");
            opts.SetSqliteDocDbMessageStore(dbPath);
        });

        // Register native platform speech services
        builder.Services.AddSpeechServices();

        // Audio capture/playback + the live mic monitor (IAudioMonitor) and route enumeration
        // (IAudioDevices) used by the Microphone sample. TryAdd-based, so it's safe alongside
        // AddSpeechServices (which already registers the source/player).
        builder.Services.AddAudioServices();
        
        // To use a 3rd-party cloud provider instead of native speech, uncomment one of these.
        // Once active, the Settings page shows an editable "API Credentials" section (the config
        // objects are mutable singletons, so keys can be changed at runtime).
        // You can even pass an empty key here and paste it in Settings afterwards.
        // builder.Services.AddAzureSpeech("your-key", "your-region");
        // builder.Services.AddElevenLabsSpeech("your-api-key");
        // builder.Services.AddOpenAiSpeech("your-api-key");
        // builder.Services.AddTypecastSpeech("your-api-key");

#if DEBUG
        builder.Logging.AddDebug();
#endif
        var app = builder.Build();

        // Apply any cloud-provider credentials saved from the Settings page before the providers are
        // first used, so a key entered in a previous session is restored on launch.
        CloudProviderCredentials.Detect(app.Services).RestoreFromStore();

        return app;
    }
}
