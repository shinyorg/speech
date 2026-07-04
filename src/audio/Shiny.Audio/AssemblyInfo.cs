using System.Runtime.CompilerServices;

// Shiny.Speech reuses the shared Android/browser helpers (PermissionRequestFragment, BrowserJsModule)
// that moved into Shiny.Audio when audio capture/playback was extracted into its own package.
[assembly: InternalsVisibleTo("Shiny.Speech")]
