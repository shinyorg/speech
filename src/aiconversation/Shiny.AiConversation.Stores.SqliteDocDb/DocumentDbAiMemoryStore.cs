using System.Text;
using Shiny.DocumentDb;

namespace Shiny.AiConversation.Stores.SqliteDocDb;

/// <summary>
/// <see cref="IAiMemoryStore"/> over Shiny.DocumentDb, with version-checked saves.
/// </summary>
public class DocumentDbAiMemoryStore(IDocumentStore store) : IAiMemoryStore
{
    static AiConversationStoreJsonContext Json => AiConversationStoreJsonContext.Default;


    public async Task<AiMemoryDocument?> Get(MemoryScope scope, string? scopeKey, string path, CancellationToken ct = default)
    {
        var wrapper = await store.Get(Id(scope, scopeKey, path), Json.StoredAiMemory, ct).ConfigureAwait(false);
        return Unwrap(wrapper);
    }


    public async Task<IReadOnlyList<AiMemoryDocument>> List(MemoryScope? scope = null, string? scopeKey = null, string? pathPrefix = null, CancellationToken ct = default)
    {
        var q = store.Query(Json.StoredAiMemory);
        if (scope is { } s)
        {
            var name = s.ToString();
            q = q.Where(x => x.Scope == name);
        }
        if (scopeKey != null)
            q = q.Where(x => x.ScopeKey == scopeKey);

        var list = await q.OrderByDescending(x => x.UpdatedTicks).ToList(ct).ConfigureAwait(false);
        return list
            .Where(x => pathPrefix == null || x.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
            .Select(x => Unwrap(x)!)
            .ToList();
    }


    public async Task Save(AiMemoryDocument document, long? expectedVersion, CancellationToken ct = default)
    {
        var wrapper = Wrap(document);
        if (expectedVersion == null)
        {
            try
            {
                await store.Insert(wrapper, Json.StoredAiMemory, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (await store.Get(wrapper.Id, Json.StoredAiMemory, ct).ConfigureAwait(false) != null)
                    throw new MemoryConflictException(document.Path);

                throw;
            }
            return;
        }

        wrapper.Version = checked((int)expectedVersion.Value);
        try
        {
            await store.Update(wrapper, Json.StoredAiMemory, ct).ConfigureAwait(false);
        }
        catch (ConcurrencyException)
        {
            throw new MemoryConflictException(document.Path);
        }
        catch (Exception)
        {
            if (await store.Get(wrapper.Id, Json.StoredAiMemory, ct).ConfigureAwait(false) == null)
                throw new MemoryConflictException(document.Path);

            throw;
        }
    }


    public Task Delete(MemoryScope scope, string? scopeKey, string path, CancellationToken ct = default)
        => store.Remove<StoredAiMemory>(Id(scope, scopeKey, path), ct);


    public Task Clear(MemoryScope scope, string? scopeKey = null, CancellationToken ct = default)
    {
        var name = scope.ToString();
        var q = store.Query(Json.StoredAiMemory).Where(x => x.Scope == name);
        if (scopeKey != null)
            q = q.Where(x => x.ScopeKey == scopeKey);

        return q.ExecuteDelete(ct);
    }


    public async Task<long> GetScopeSize(MemoryScope scope, string? scopeKey, CancellationToken ct = default)
    {
        var name = scope.ToString();
        var key = scopeKey ?? "";
        var list = await store.Query(Json.StoredAiMemory).Where(x => x.Scope == name && x.ScopeKey == key).ToList(ct).ConfigureAwait(false);
        return list.Sum(x => (long)x.Size);
    }


    static string Id(MemoryScope scope, string? scopeKey, string path) => $"{scope}|{scopeKey}|{path}";

    static StoredAiMemory Wrap(AiMemoryDocument doc) => new()
    {
        Id = Id(doc.Scope, doc.ScopeKey, doc.Path),
        Version = checked((int)doc.Version),
        Scope = doc.Scope.ToString(),
        ScopeKey = doc.ScopeKey ?? "",
        Path = doc.Path,
        UpdatedTicks = doc.Updated.UtcTicks,
        Size = Encoding.UTF8.GetByteCount(doc.Content),
        Memory = doc
    };

    static AiMemoryDocument? Unwrap(StoredAiMemory? wrapper)
        => wrapper == null ? null : wrapper.Memory with { Version = wrapper.Version };
}
