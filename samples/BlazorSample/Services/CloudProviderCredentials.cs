using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Shiny.Speech.ElevenLabs;
using Shiny.Speech.OpenAI;
using Shiny.Speech.Typecast;

namespace BlazorSample.Services;

/// <summary>
/// Persists cloud-provider credentials in the browser's <c>localStorage</c> (the WASM "preferences
/// store") so they survive reloads. A production app should avoid storing raw secrets in localStorage.
/// </summary>
static class CredentialStore
{
    const string Prefix = "speech.cred.";

    public static async Task<string?> GetAsync(IJSRuntime js, string key)
        => await js.InvokeAsync<string?>("localStorage.getItem", Prefix + key);

    public static async Task SetAsync(IJSRuntime js, string key, string value)
        => await js.InvokeVoidAsync("localStorage.setItem", Prefix + key, value);
}


/// <summary>One editable credential value bound to a live provider config property.</summary>
public sealed class CredentialField(string storeKey, string label, Func<string> read, Action<string> write)
{
    public string StoreKey { get; } = storeKey;
    public string Label { get; } = label;
    public string Value { get; set; } = read();

    /// <summary>Writes the current <see cref="Value"/> to the live config (a mutable singleton).</summary>
    public void ApplyToConfig() => write(this.Value ?? String.Empty);

    /// <summary>Applies a persisted value to both the live config and this field.</summary>
    public void SetFromStore(string saved)
    {
        write(saved);
        this.Value = saved;
    }
}


/// <summary>
/// Detects whichever 3rd-party cloud speech provider(s) are registered and exposes their credentials
/// as editable, localStorage-backed fields. The provider config objects are mutable singletons
/// (Shiny.Speech supports runtime key changes), so applying a field updates the live config — picked
/// up on the provider's next call — and saving persists it for the next launch.
///
/// Registered as a singleton so the startup restore (in the layout) and the Settings page share one
/// instance / one set of fields.
/// </summary>
public sealed class CloudProviderCredentials
{
    public string? ProviderName { get; private set; }
    public List<CredentialField> Fields { get; } = [];
    public bool IsActive => this.Fields.Count > 0;

    /// <summary>Loads persisted credentials from localStorage into the live configs + fields.</summary>
    public async Task RestoreFromStoreAsync(IJSRuntime js)
    {
        foreach (var field in this.Fields)
        {
            var saved = await CredentialStore.GetAsync(js, field.StoreKey);
            if (saved != null)
                field.SetFromStore(saved);
        }
    }

    /// <summary>Applies every field to its config and persists it to localStorage.</summary>
    public async Task SaveToStoreAsync(IJSRuntime js)
    {
        foreach (var field in this.Fields)
        {
            field.ApplyToConfig();
            await CredentialStore.SetAsync(js, field.StoreKey, field.Value ?? String.Empty);
        }
    }

    public static CloudProviderCredentials Detect(IServiceProvider services)
    {
        var result = new CloudProviderCredentials();
        var names = new List<string>();

        if (services.GetService<ElevenLabsConfig>() is { } eleven)
        {
            names.Add("ElevenLabs");
            result.Fields.Add(new("elevenlabs.apiKey", "ElevenLabs — API Key", () => eleven.ApiKey, v => eleven.ApiKey = v));
        }
        if (services.GetService<OpenAiSpeechConfig>() is { } openai)
        {
            names.Add("OpenAI");
            result.Fields.Add(new("openai.apiKey", "OpenAI — API Key", () => openai.ApiKey, v => openai.ApiKey = v));
        }
        if (services.GetService<TypecastConfig>() is { } typecast)
        {
            names.Add("Typecast");
            result.Fields.Add(new("typecast.apiKey", "Typecast — API Key", () => typecast.ApiKey, v => typecast.ApiKey = v));
        }

        result.ProviderName = names.Count switch
        {
            0 => null,
            1 => names[0],
            _ => String.Join(" + ", names)
        };
        return result;
    }
}
