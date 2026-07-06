namespace Shiny.Speech.Azure;

/// <summary>
/// Azure Speech credentials. Properties are mutable so they can be changed at runtime — the
/// provider reads them on each call, so updating <see cref="SubscriptionKey"/> or <see cref="Region"/>
/// takes effect on the next synthesis/recognition without re-registering anything.
/// </summary>
public record AzureSpeechConfig
{
    public required string SubscriptionKey { get; set; }
    public required string Region { get; set; }
}
