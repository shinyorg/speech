using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.AiConversation.Infrastructure;

/// <summary>
/// Stateless: holds no sessions, no registry and no "current" pointer. Every load is a store call that
/// returns a new instance.
/// </summary>
public class AiSessionManager(
    IAiSessionStore store,
    IChatClientProvider chatClientProvider,
    IEnumerable<ISessionInterceptor> interceptors,
    TimeProvider timeProvider,
    IAiMemoryStore? memoryStore = null,
    AiMemoryOptions? memoryOptions = null,
    IContextCompactor? compactor = null,
    ILogger<AiSessionManager>? logger = null
) : IAiSessionManager
{
    readonly IReadOnlyList<ISessionInterceptor> interceptors = interceptors.ToList();
    readonly IAiMemoryStore memoryStore = memoryStore ?? new InMemoryAiMemoryStore();
    readonly AiMemoryOptions memoryOptions = memoryOptions ?? new AiMemoryOptions();
    readonly IContextCompactor compactor = compactor ?? new ChatClientContextCompactor(chatClientProvider);
    readonly ILogger logger = logger ?? NullLogger<AiSessionManager>.Instance;
    IAiMemoryService? memory;


    public IAiMemoryService Memory => this.memory ??= new AiMemoryService(this.memoryStore, timeProvider);


    public async Task<IAiSession> Start(AiSessionSettings? settings = null, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var header = new AiSessionDocument(
            IdGenerator.New(timeProvider),
            AiSessionDocument.CurrentSchemaVersion,
            1,
            null,
            now,
            now,
            settings ?? new AiSessionSettings(),
            null,
            [],
            SessionUsage.Empty,
            ContextSnapshot.Empty,
            0,
            0
        );
        await store.CreateSession(header, ct).ConfigureAwait(false);
        return await this.Create(header, [], true, ct).ConfigureAwait(false);
    }


    public async Task<IAiSession?> Get(string sessionId, CancellationToken ct = default)
    {
        var header = await store.GetSession(sessionId, ct).ConfigureAwait(false);
        return header == null ? null : await this.Load(header, ct).ConfigureAwait(false);
    }


    public async Task<IAiSession?> GetLatest(CancellationToken ct = default)
    {
        var latest = await store.QuerySessions(new SessionQuery(Take: 1), ct).ConfigureAwait(false);
        return latest.Count == 0 ? null : await this.Load(latest[0], ct).ConfigureAwait(false);
    }


    public Task<IReadOnlyList<AiSessionDocument>> Query(SessionQuery query, CancellationToken ct = default)
        => store.QuerySessions(query, ct);


    public async Task Delete(string sessionId, CancellationToken ct = default)
    {
        await store.DeleteSession(sessionId, ct).ConfigureAwait(false);
        await this.memoryStore.Clear(MemoryScope.Session, sessionId, ct).ConfigureAwait(false);
    }


    async Task<IAiSession> Load(AiSessionDocument header, CancellationToken ct)
    {
        var messages = await SessionLoader.LoadWorkingSet(store, header.SessionId, ct).ConfigureAwait(false);
        return await this.Create(header, messages, false, ct).ConfigureAwait(false);
    }


    async Task<IAiSession> Create(AiSessionDocument header, List<AiMessageDocument> messages, bool isNew, CancellationToken ct)
    {
        var session = new AiSession(
            header,
            messages,
            store,
            chatClientProvider,
            this.interceptors,
            timeProvider,
            this.compactor,
            this.memoryStore,
            this.memoryOptions,
            this.logger
        );
        await session.Load(isNew, ct).ConfigureAwait(false);
        return session;
    }
}
