using System.Runtime.CompilerServices;

// Shiny.Speech reuses the shared browser helper (BrowserJsModule) that lives in Shiny.Audio.
// Android activity tracking + permission requests now come from Shiny.Core (AndroidPlatform).
[assembly: InternalsVisibleTo("Shiny.Speech")]
