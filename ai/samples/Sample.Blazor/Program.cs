using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Sample.Blazor.Services;
using Shiny;
using Shiny.AiConversation;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Sample.Blazor.Components.App>("#app");

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

var app = builder.Build();

await app.RunAsync();
