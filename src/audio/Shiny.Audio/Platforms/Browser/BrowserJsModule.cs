using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Shiny.Audio;

[SupportedOSPlatform("browser")]
internal static class BrowserJsModule
{
    const string ModuleName = "shiny-speech";
    // Static web asset shipped in the Shiny.Audio package. The path is relative to the
    // WASM runtime (_framework/), so "../" reaches the app root where _content is served.
    const string ModulePath = "../_content/Shiny.Audio/shiny-audio.js";

    static Task? importTask;

    internal static Task ImportAsync()
    {
        importTask ??= JSHost.ImportAsync(ModuleName, ModulePath);
        return importTask;
    }
}
