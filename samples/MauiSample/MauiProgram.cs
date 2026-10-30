using MauiSample.Features.Settings;
using MauiSample.Services;
using Microsoft.Extensions.Logging;
using Shiny;

namespace MauiSample;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if DEBUG
        // print managed crashes to the device console (the native crash report doesn't carry the message)
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.WriteLine($"[unhandled] {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Console.WriteLine($"[unobserved] {e.Exception}");
#endif
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseShiny() // required: native speech/audio permission handling routes through Shiny.Core (AndroidPlatform)
            .UseShinyControls()
            .UseShinyCamera()
            .UseShinyShell(cfg => cfg.AddGeneratedMaps())
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // AI Conversation (v3 sessions): persisted in SQLite, with titles, conversation search and long-term memory
        builder.Services.AddShinyAiConversation(opts =>
        {
            opts.AddGithubCopilotChatClient();
            opts.SetSqliteDocDbStores(Path.Combine(FileSystem.AppDataDirectory, "sample_ai.db"));
            opts.UseFileAttachments(Path.Combine(FileSystem.AppDataDirectory, "ai_attachments"));
            opts.AddInterceptor<SampleInterceptor>();
            opts.AddSessionTitles();
            opts.AddConversationSearch();
            opts.AddLongTermMemory();
        });

        // Voice over the session: push-to-talk, wake word, interruption, sentence-streamed speech.
        // It never registers speech engines - the native ones are registered below.
        builder.Services.AddShinyAiConversationVoice(voice => voice.AddVoiceSelectionTools());
        builder.Services.AddSingleton<ConversationHost>();

        // The assistant can ask to use the camera ("point it at the label"); it asks first and shows the purpose.
        builder.Services.AddAiConversationCamera();

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
