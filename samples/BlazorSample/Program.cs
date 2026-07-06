using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Shiny;
using Shiny.AiConversation;
using BlazorSample;
using BlazorSample.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Native (browser) speech services
builder.Services.AddSpeechServices();

// To use a 3rd-party cloud provider instead of native browser speech, uncomment one of these.
// Once active, the Settings page shows an editable "API Credentials" section whose values persist in
// localStorage across reloads (the config objects are mutable singletons, so keys change at runtime).
// You can pass an empty key here and paste it in Settings afterwards.
// builder.Services.AddElevenLabsSpeech("your-api-key");
// builder.Services.AddOpenAiSpeech("your-api-key");
// builder.Services.AddTypecastSpeech("your-api-key");

// Detects the active cloud provider (if any) and backs its credentials with localStorage.
builder.Services.AddSingleton(sp => CloudProviderCredentials.Detect(sp));

// AI Conversation
builder.Services.AddSingleton<InMemoryMessageStore>();
builder.Services.AddSingleton<IContextProvider, SampleContextProvider>();
builder.Services.AddShinyAiConversation(opts =>
{
    opts.AddStaticOpenAIChatClient(
        "YOUR API KEY HERE",
        "https://api.openai.com/v1",
        "gpt-4o"
    );
    opts.SetMessageStore<InMemoryMessageStore>();
});

await builder.Build().RunAsync();
