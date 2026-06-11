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
