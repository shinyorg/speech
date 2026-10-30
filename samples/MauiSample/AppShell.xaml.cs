using Shiny;

namespace MauiSample;

public partial class AppShell : ShinyShell
{
    public AppShell()
    {
        InitializeComponent();

#if DEBUG
        // Dev harness: launch straight into a tab (e.g. SAMPLE_START_ROUTE=//main/chat via
        // `xcrun devicectl device process launch --environment-variables`) to reproduce issues without tapping.
        if (Environment.GetEnvironmentVariable("SAMPLE_START_ROUTE") is { Length: > 0 } route)
            this.Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(1), async () => await this.GoToAsync(route));
#endif
    }
}
