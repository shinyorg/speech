using System.Text;

namespace Shiny.AiConversation;

/// <summary>
/// Keeps memory documents in memory only. The default memory store.
/// </summary>
public class InMemoryAiMemoryStore : IAiMemoryStore
{
    readonly Lock sync = new();
    readonly Dictionary<(MemoryScope, string?, string), AiMemoryDocument> docs = new();

    public Task<AiMemoryDocument?> Get(MemoryScope scope, string? scopeKey, string path, CancellationToken ct = default)
    {
        lock (this.sync)
            return Task.FromResult(this.docs.GetValueOrDefault((scope, scopeKey, path)));
    }

    public Task<IReadOnlyList<AiMemoryDocument>> List(MemoryScope? scope = null, string? scopeKey = null, string? pathPrefix = null, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            IReadOnlyList<AiMemoryDocument> list = this.docs.Values
                .Where(x => scope == null || x.Scope == scope)
                .Where(x => scopeKey == null || x.ScopeKey == scopeKey)
                .Where(x => pathPrefix == null || x.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
                .OrderByDescending(x => x.Updated)
                .ToList();

            return Task.FromResult(list);
        }
    }

    public Task Save(AiMemoryDocument document, long? expectedVersion, CancellationToken ct = default)
    {
        var key = (document.Scope, document.ScopeKey, document.Path);
        lock (this.sync)
        {
            var exists = this.docs.TryGetValue(key, out var current);
            if (expectedVersion == null ? exists : !exists || current!.Version != expectedVersion)
                throw new MemoryConflictException(document.Path);

            this.docs[key] = document;
        }
        return Task.CompletedTask;
    }

    public Task Delete(MemoryScope scope, string? scopeKey, string path, CancellationToken ct = default)
    {
        lock (this.sync)
            this.docs.Remove((scope, scopeKey, path));

        return Task.CompletedTask;
    }

    public Task Clear(MemoryScope scope, string? scopeKey = null, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            var keys = this.docs.Keys.Where(k => k.Item1 == scope && (scopeKey == null || k.Item2 == scopeKey)).ToList();
            foreach (var key in keys)
                this.docs.Remove(key);
        }
        return Task.CompletedTask;
    }

    public Task<long> GetScopeSize(MemoryScope scope, string? scopeKey, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            var size = this.docs.Values
                .Where(x => x.Scope == scope && x.ScopeKey == scopeKey)
                .Sum(x => (long)Encoding.UTF8.GetByteCount(x.Content));

            return Task.FromResult(size);
        }
    }
}
