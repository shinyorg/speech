using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;
using Shiny.Speech.Azure;
using Shiny.Speech.ElevenLabs;
using Shiny.Speech.OpenAI;
using Shiny.Speech.Typecast;

namespace MauiSample.Features.Settings;

/// <summary>
/// Persists cloud-provider credentials in the MAUI <see cref="Preferences"/> store so they survive
/// app restarts. (A production app would use <c>SecureStorage</c> for secrets — Preferences keeps the
/// sample simple.)
/// </summary>
static class CredentialStore
{
    const string Prefix = "speech.cred.";

    public static string? Get(string key) => Preferences.Default.Get<string?>(Prefix + key, null);
    public static void Set(string key, string value) => Preferences.Default.Set(Prefix + key, value);
}


/// <summary>One editable credential value bound to a live provider config property and backed by the store.</summary>
public partial class CredentialField : ObservableObject
{
    readonly string storeKey;
    readonly Func<string> read;
    readonly Action<string> write;

    public CredentialField(string storeKey, string label, Func<string> read, Action<string> write)
    {
        this.storeKey = storeKey;
        this.Label = label;
        this.read = read;
        this.write = write;
        this.value = read();
    }

    public string Label { get; }

    [ObservableProperty]
    string value;

    /// <summary>Applies a persisted value (if any) to the live config and this field.</summary>
    public void Restore()
    {
        var saved = CredentialStore.Get(this.storeKey);
        if (saved != null)
        {
            this.write(saved);
            this.Value = saved;
        }
    }

    /// <summary>Re-reads the current value from the config.</summary>
    public void Reload() => this.Value = this.read();

    /// <summary>Writes the edited value to the live config (a mutable singleton) and persists it.</summary>
    public void Commit()
    {
        var v = this.Value ?? String.Empty;
        this.write(v);
        CredentialStore.Set(this.storeKey, v);
    }
}


/// <summary>
/// Detects whichever 3rd-party cloud speech provider(s) are registered and exposes their credentials
/// as editable, store-backed fields. The provider config objects are mutable singletons (Shiny.Speech
/// supports runtime key changes), so <see cref="CredentialField.Commit"/> updates the live config —
/// picked up on the provider's next call — and saves it for the next launch.
/// </summary>
public sealed class CloudProviderCredentials
{
    public string? ProviderName { get; private set; }
    public List<CredentialField> Fields { get; } = [];
    public bool IsActive => this.Fields.Count > 0;

    /// <summary>Applies any persisted credentials to the live configs. Call once at startup.</summary>
    public void RestoreFromStore()
    {
        foreach (var field in this.Fields)
            field.Restore();
    }

    public static CloudProviderCredentials Detect(IServiceProvider services)
    {
        var result = new CloudProviderCredentials();
        var names = new List<string>();

        if (services.GetService<AzureSpeechConfig>() is { } azure)
        {
            names.Add("Azure");
            result.Fields.Add(new("azure.subscriptionKey", "Azure — Subscription Key", () => azure.SubscriptionKey, v => azure.SubscriptionKey = v));
            result.Fields.Add(new("azure.region", "Azure — Region", () => azure.Region, v => azure.Region = v));
        }
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
