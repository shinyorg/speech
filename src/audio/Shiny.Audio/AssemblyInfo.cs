using System.Runtime.CompilerServices;

// Shiny.Speech reuses the shared browser helper (BrowserJsModule) that lives in Shiny.Audio.
// Android activity tracking + permission requests now come from Shiny.Core (AndroidPlatform).
[assembly: InternalsVisibleTo("Shiny.Speech")]

// Shiny.Audio.Linux ships out-of-band (it pulls a managed MP3 decoder in), but is otherwise a
// first-party platform backend and reuses the shared AudioLevelThrottle meter pacing.
[assembly: InternalsVisibleTo("Shiny.Audio.Linux")]
