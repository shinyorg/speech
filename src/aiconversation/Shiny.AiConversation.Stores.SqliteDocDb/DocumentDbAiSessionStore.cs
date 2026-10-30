using Shiny.DocumentDb;

namespace Shiny.AiConversation.Stores.SqliteDocDb;

/// <summary>
/// <see cref="IAiSessionStore"/> over Shiny.DocumentDb. Header saves are version-checked by the database,
/// message sequences come from a versioned per-session counter written in the same transaction as the
/// message, and search uses the database's full-text index. Configure the document store with
/// <see cref="AiConversationDocumentStoreExtensions.MapAiConversation"/>.
/// </summary>
public class DocumentDbAiSessionStore(IDocumentStore store) : IAiSessionStore
{
    const int MaxAppendAttempts = 10;

    static AiConversationStoreJsonContext Json => AiConversationStoreJsonContext.Default;

    readonly SemaphoreSlim initLock = new(1, 1);
    readonly SemaphoreSlim appendLock = new(1, 1);
    bool initialized;


    public async Task CreateSession(AiSessionDocument session, CancellationToken ct = default)
    {
        await this.EnsureInitialized(ct).ConfigureAwait(false);
        await store.Insert(Wrap(session), Json.StoredAiSession, ct).ConfigureAwait(false);
    }


    public async Task SaveSession(AiSessionDocument session, long expectedVersion, CancellationToken ct = default)
    {
        // the database checks the stored version against the wrapper's and increments it
        var wrapper = Wrap(session);
        wrapper.Version = checked((int)expectedVersion);
        try
        {
            await store.Update(wrapper, Json.StoredAiSession, ct).ConfigureAwait(false);
        }
        catch (ConcurrencyException)
        {
            throw new SessionConflictException(session.SessionId);
        }
        catch (Exception)
        {
            // deleted underneath us
            if (await store.Get(session.SessionId, Json.StoredAiSession, ct).ConfigureAwait(false) == null)
                throw new SessionConflictException(session.SessionId);

            throw;
        }
    }


    public async Task<AiSessionDocument?> GetSession(string sessionId, CancellationToken ct = default)
    {
        var wrapper = await store.Get(sessionId, Json.StoredAiSession, ct).ConfigureAwait(false);
        return Unwrap(wrapper);
    }


    public async Task<IReadOnlyList<AiSessionDocument>> QuerySessions(SessionQuery query, CancellationToken ct = default)
    {
        var q = store.Query(Json.StoredAiSession);

        if (!String.IsNullOrWhiteSpace(query.Text))
        {
            var text = query.Text;
            q = q.Where(x => x.Title!.Contains(text) || x.Summary!.Contains(text));
        }
        if (query.From is { } from)
        {
            var ticks = from.UtcTicks;
            q = q.Where(x => x.LastActivityTicks >= ticks);
        }
        if (query.To is { } to)
        {
            var ticks = to.UtcTicks;
            q = q.Where(x => x.LastActivityTicks <= ticks);
        }
        if (query.Profile is { } profile)
            q = q.Where(x => x.Profile == profile);

        q = q.OrderByDescending(x => x.LastActivityTicks);

        // tags live in a list inside the nested document: filter those in memory
        if (query.Tags is { Count: > 0 } tags)
        {
            var all = await q.ToList(ct).ConfigureAwait(false);
            return all
                .Select(x => Unwrap(x)!)
                .Where(x => tags.All(t => x.Settings.Tags.Contains(t)))
                .Skip(query.Skip)
                .Take(query.Take)
                .ToList();
        }

        var page = await q.Paginate(query.Skip, query.Take).ToList(ct).ConfigureAwait(false);
        return page.Select(x => Unwrap(x)!).ToList();
    }


    public async Task DeleteSession(string sessionId, CancellationToken ct = default)
    {
        await store.Query(Json.StoredAiMessage).Where(x => x.SessionId == sessionId).ExecuteDelete(ct).ConfigureAwait(false);
        await store.Remove<StoredAiSequence>(sessionId, ct).ConfigureAwait(false);
        await store.Remove<StoredAiSession>(sessionId, ct).ConfigureAwait(false);
    }


    public async Task<AiMessageDocument> AppendMessage(AiMessageDocument message, CancellationToken ct = default)
    {
        await this.EnsureInitialized(ct).ConfigureAwait(false);

        // the in-process lock avoids pointless retries; the versioned counter makes it safe across processes
        await this.appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var counter = await store.Get(message.SessionId, Json.StoredAiSequence, ct).ConfigureAwait(false);
                var next = (counter?.Next ?? 0) + 1;
                var stored = message with { Sequence = next };

                var unit = store.CreateUnitOfWork();
                if (counter == null)
                {
                    unit.Add(new StoredAiSequence { Id = message.SessionId, Next = next }, Json.StoredAiSequence);
                }
                else
                {
                    counter.Next = next;
                    unit.Update(counter, Json.StoredAiSequence);
                }
                unit.Add(Wrap(stored), Json.StoredAiMessage);

                try
                {
                    await unit.SaveChanges(ct).ConfigureAwait(false);
                    return stored;
                }
                catch (Exception) when (attempt < MaxAppendAttempts && !ct.IsCancellationRequested)
                {
                    // another writer took the sequence (version conflict or duplicate counter); the unit
                    // rolled back, so nothing was written - try the next one
                }
            }
        }
        finally
        {
            this.appendLock.Release();
        }
    }


    public Task UpdateMessage(AiMessageDocument message, CancellationToken ct = default)
        => store.Update(Wrap(message), Json.StoredAiMessage, ct);


    public async Task<IReadOnlyList<AiMessageDocument>> GetMessages(MessageQuery query, CancellationToken ct = default)
    {
        var sessionId = query.SessionId;
        var q = store.Query(Json.StoredAiMessage).Where(x => x.SessionId == sessionId);

        if (!query.IncludeCompacted)
            q = q.Where(x => !x.Compacted);

        if (query.AfterSequence is { } after)
            q = q.Where(x => x.Sequence > after);

        if (query.BeforeSequence is { } before)
            q = q.Where(x => x.Sequence < before);

        if (query.Limit is not { } limit)
        {
            var all = await q.OrderBy(x => x.Sequence).ToList(ct).ConfigureAwait(false);
            return all.Select(x => x.Message).ToList();
        }

        // paging backwards takes the newest messages before the cursor; otherwise the oldest after it
        if (query.BeforeSequence != null || query.AfterSequence == null)
        {
            var newest = await q.OrderByDescending(x => x.Sequence).Paginate(0, limit).ToList(ct).ConfigureAwait(false);
            return newest.Select(x => x.Message).Reverse().ToList();
        }

        var oldest = await q.OrderBy(x => x.Sequence).Paginate(0, limit).ToList(ct).ConfigureAwait(false);
        return oldest.Select(x => x.Message).ToList();
    }


    public async Task<IReadOnlyList<MessageSearchHit>> SearchMessages(MessageSearchQuery query, CancellationToken ct = default)
    {
        var terms = SearchText.Terms(query.Text);
        if (terms.Length == 0)
            return [];

        // full-text match with the cheap predicates pushed down; the rest is filtered on the (small) result
        var sessionId = query.SessionId;
        var overFetch = Math.Max(query.Limit * 4, 20);
        var results = sessionId == null
            ? await store.FullTextSearch<StoredAiMessage>(String.Join(' ', terms), overFetch, x => x.Searchable, ct).ConfigureAwait(false)
            : await store.FullTextSearch<StoredAiMessage>(String.Join(' ', terms), overFetch, x => x.Searchable && x.SessionId == sessionId, ct).ConfigureAwait(false);

        var headers = new Dictionary<string, AiSessionDocument?>();
        var hits = new List<MessageSearchHit>();
        foreach (var result in results)
        {
            var msg = result.Document;
            if (!msg.Searchable || (sessionId != null && msg.SessionId != sessionId) || msg.SessionId == query.ExcludeSessionId)
                continue;

            var timestamp = msg.Message.Timestamp;
            if ((query.From != null && timestamp < query.From) || (query.To != null && timestamp > query.To))
                continue;

            if (query.ExcludeFromSequence != null && msg.Sequence >= query.ExcludeFromSequence)
                continue;

            if (!headers.TryGetValue(msg.SessionId, out var header))
            {
                header = await this.GetSession(msg.SessionId, ct).ConfigureAwait(false);
                headers[msg.SessionId] = header;
            }
            if (header == null || header.Settings.ExcludeFromSearch)
                continue;

            hits.Add(new MessageSearchHit(
                msg.SessionId,
                header.Title,
                msg.Sequence,
                msg.Message.Role,
                timestamp,
                SearchText.Snippet(msg.Text ?? String.Empty, terms),
                result.Score
            ));

            if (hits.Count >= query.Limit)
                break;
        }
        return hits;
    }


    public async Task Purge(DateTimeOffset before, CancellationToken ct = default)
    {
        var ticks = before.UtcTicks;
        var old = await store.Query(Json.StoredAiSession).Where(x => x.LastActivityTicks < ticks).ToList(ct).ConfigureAwait(false);
        foreach (var session in old)
            await this.DeleteSession(session.Id, ct).ConfigureAwait(false);
    }


    async Task EnsureInitialized(CancellationToken ct)
    {
        if (this.initialized)
            return;

        await this.initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this.initialized)
                return;

            if (store is DocumentStore docStore)
            {
                await docStore.CreateIndexAsync<StoredAiMessage>(x => x.SessionId, Json.StoredAiMessage, ct).ConfigureAwait(false);
                await docStore.CreateIndexAsync<StoredAiMessage>(x => x.Sequence, Json.StoredAiMessage, ct).ConfigureAwait(false);
                await docStore.CreateIndexAsync<StoredAiSession>(x => x.LastActivityTicks, Json.StoredAiSession, ct).ConfigureAwait(false);
            }
            this.initialized = true;
        }
        finally
        {
            this.initLock.Release();
        }
    }


    static StoredAiSession Wrap(AiSessionDocument session) => new()
    {
        Id = session.SessionId,
        Version = checked((int)session.Version),
        LastActivityTicks = session.LastActivity.UtcTicks,
        Profile = session.Settings.Profile,
        Title = session.Title,
        Summary = session.Summary,
        Session = session
    };

    static AiSessionDocument? Unwrap(StoredAiSession? wrapper)
        => wrapper == null ? null : wrapper.Session with { Version = wrapper.Version };

    static StoredAiMessage Wrap(AiMessageDocument message) => new()
    {
        Id = message.MessageId,
        SessionId = message.SessionId,
        Sequence = message.Sequence,
        Compacted = message.Compacted,
        Searchable = message.Role is MessageRole.User or MessageRole.Assistant && !String.IsNullOrWhiteSpace(message.Text),
        TimestampTicks = message.Timestamp.UtcTicks,
        Text = message.Text,
        Message = message
    };
}
