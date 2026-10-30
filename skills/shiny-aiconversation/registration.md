# Registration & Configuration

## DI Registration

Register with `AddShinyAiConversation()` (namespace `Shiny`). The configure callback is optional.

```csharp
using Shiny;
using Shiny.AiConversation;

var builder = MauiApp.CreateBuilder();   // or any IServiceCollection host

builder.Services.AddShinyAiConversation(opts =>
{
    // Choose one chat client approach:

    // Option A: Register IChatClient in DI (resolved by the default provider)
    // builder.Services.AddChatClient(new OpenAIClient("key").GetChatClient("gpt-4o").AsIChatClient());

    // Option B: Static OpenAI-compatible provider (NuGet: Shiny.AiConversation.OpenAi)
    // opts.AddStaticOpenAIChatClient("your-api-key", "https://api.openai.com/v1", "gpt-4o");

    // Option C: GitHub Copilot with device code auth (NuGet: Shiny.AiConversation.Maui.GithubCopilot)
    opts.AddGithubCopilotChatClient();

    // Optional: persistent store (default keeps sessions in memory)
    // opts.SetSessionStore<MySessionStore>();

    // Optional: tools and prompts
    opts.AddInterceptor<AssistantInterceptor>();
    opts.AddTool(AIFunctionFactory.Create(GetWeather, "get_weather"));
});

var app = builder.Build();
```

Then inject `IAiSessionManager` wherever you need a session.

## AiConversationOptions

### SetChatClientProvider<T>()
- **Optional** - without it, `InjectedChatClientProvider` resolves `IChatClient` from DI
- Registers `T` as a singleton `IChatClientProvider` (`TryAddSingleton`, first registration wins)

### SetSessionStore<T>()
- **Optional** - defaults to `InMemoryAiSessionStore` (nothing survives a restart)
- Registers `T` as a singleton `IAiSessionStore` (`TryAddSingleton`)

### AddInterceptor<T>()
- Registers an `ISessionInterceptor` singleton; multiple are supported and run in registration order
- See `interceptors.md`

### AddTool(AITool tool, AiToolFlags flags = None, params string[] profiles)
- Adds the tool to every session, or only to sessions whose `AiSessionSettings.Profile` is in `profiles`
- `AiToolFlags.Silent` marks tools that should never trigger a spoken "one moment" acknowledgement (surfaced as `ToolCallStarted.Silent`)
- Plain `AITool` singletons registered directly in DI are also added to every session

## Auto-Registered Dependencies

`AddShinyAiConversation()` registers (via `TryAdd`, so register your own first to override):
- `TimeProvider.System`
- `InjectedChatClientProvider` as `IChatClientProvider`
- `InMemoryAiSessionStore` as `IAiSessionStore`
- `AiSessionManager` as `IAiSessionManager`
- `DefaultSessionInterceptor` - adds tools from `AddTool` / DI to each session and the current local time as a system prompt on every turn

It does **not** register any speech or audio services.

## Per-Session Settings

`AiSessionSettings` is persisted with the session and passed to `Start`:

```csharp
var session = await manager.Start(new AiSessionSettings
{
    Profile = "cooking",           // interceptors / AddTool profiles key off this
    ContextWindow = 128_000,       // enables context pressure; falls back to IChatClientProvider.ContextWindow
    ContextWarningThreshold = 0.75,
    ContextCriticalThreshold = 0.90,
    Tags = ["kitchen"],
    ExcludeFromSearch = false,
    Metadata = new Dictionary<string, string> { ["recipeId"] = "42" }
});

await session.UpdateSettings(s => s with { Tags = ["kitchen", "dinner"] });
```
