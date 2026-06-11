using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Shiny.Speech;

[SupportedOSPlatform("browser")]
internal static class BrowserJsModule
{
    const string ModuleName = "shiny-speech";
    const string ModulePath = "../shiny-speech.js";

    static Task? importTask;

    internal static Task ImportAsync()
    {
        importTask ??= JSHost.ImportAsync(ModuleName, ModulePath);
        return importTask;
    }
}
