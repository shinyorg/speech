namespace Shiny.Speech.Cloud;

/// <summary>
/// Holds a lazily-created SDK/HTTP client and transparently rebuilds it whenever the credential it
/// was built from changes. This lets a provider reuse an expensive client while still honouring
/// runtime API-key updates (e.g. a mutable config whose <c>ApiKey</c> is changed after startup).
/// </summary>
/// <typeparam name="T">The client type. If it implements <see cref="IDisposable"/> the previous
/// instance is disposed when a rebuild occurs and when this holder is disposed.</typeparam>
/// <param name="factory">Builds a new client from the current configuration.</param>
public sealed class RefreshableClient<T>(Func<T> factory) : IDisposable where T : class
{
    readonly object sync = new();
    T? current;
    string? credential;

    /// <summary>
    /// Returns the cached client, rebuilding it first if <paramref name="currentCredential"/> differs
    /// from the credential the cached client was built with.
    /// </summary>
    public T Get(string currentCredential)
    {
        lock (this.sync)
        {
            if (this.current is null || !String.Equals(this.credential, currentCredential, StringComparison.Ordinal))
            {
                (this.current as IDisposable)?.Dispose();
                this.current = factory();
                this.credential = currentCredential;
            }
            return this.current;
        }
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            (this.current as IDisposable)?.Dispose();
            this.current = null;
            this.credential = null;
        }
    }
}
