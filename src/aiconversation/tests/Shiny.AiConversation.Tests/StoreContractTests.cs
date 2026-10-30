using Shiny.AiConversation.Stores.SqliteDocDb;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;

namespace Shiny.AiConversation.Tests;

/// <summary>
/// The behaviour every <see cref="IAiSessionStore"/> / <see cref="IAiMemoryStore"/> must have, run against
/// each implementation.
/// </summary>
public abstract class StoreContractTests
{
    protected abstract (IAiSessionStore Sessions, IAiMemoryStore Memory) CreateStores();

    static AiSessionDocument Header(string id, DateTimeOffset? lastActivity = null, AiSessionSettings? settings = null, string? title = null, string? summary = null) => new(
        id, 1, 1, title, DateTimeOffset.UtcNow, lastActivity ?? DateTimeOffset.UtcNow,
        settings ?? new AiSessionSettings(), summary, [], SessionUsage.Empty, ContextSnapshot.Empty, 0, 0);

    static AiMessageDocument Message(string sessionId, string text, MessageRole role = MessageRole.User) => new(
        Guid.NewGuid().ToString(), sessionId, 0, 1, DateTimeOffset.UtcNow, role,
        role == MessageRole.User ? MessageSource.Typed : MessageSource.Model,
        MessageStatus.Complete, [new TextPart(text)], text, null, null, false, null, "run");

    static AiMemoryDocument Memory(string path, MemoryScope scope = MemoryScope.Global, string? key = null, string content = "hello", long version = 1, DateTimeOffset? updated = null)
        => new(path, scope, key, "desc", content, DateTimeOffset.UtcNow, updated ?? DateTimeOffset.UtcNow, MemoryAuthor.Assistant, version, 1);


    [Test]
    public async Task Session_RoundTrips_WithVersion()
    {
        var (store, _) = this.CreateStores();
        var header = Header("a", settings: new AiSessionSettings { Profile = "p", Tags = ["t"] }) with
        {
            PendingQuestions = [new AiQuestion("q", "Which?", [new AiChoice("x", "X")])],
            Usage = new SessionUsage(1, 2, 3, 4, 5, 6)
        };
        await store.CreateSession(header);
        await store.SaveSession(header with { Version = 2, Title = "T" }, 1);

        var back = (await store.GetSession("a"))!;
        await Assert.That(back.Version).IsEqualTo(2);
        await Assert.That(back.Title).IsEqualTo("T");
        await Assert.That(back.Settings.Profile).IsEqualTo("p");
        await Assert.That(back.PendingQuestions.Single().Choices!.Single().Label).IsEqualTo("X");
        await Assert.That(back.Usage).IsEqualTo(header.Usage);
    }


    [Test]
    public async Task SaveSession_StaleOrDeleted_Conflicts()
    {
        var (store, _) = this.CreateStores();
        await store.CreateSession(Header("a"));
        await store.SaveSession(Header("a") with { Version = 2 }, 1);

        await Assert.That(() => store.SaveSession(Header("a") with { Version = 2 }, 1)).Throws<SessionConflictException>();

        await store.DeleteSession("a");
        await Assert.That(() => store.SaveSession(Header("a") with { Version = 3 }, 2)).Throws<SessionConflictException>();
    }


    [Test]
    public async Task AppendMessage_ConcurrentAppends_GetUniqueSequences()
    {
        var (store, _) = this.CreateStores();
        await store.CreateSession(Header("a"));

        var appended = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.AppendMessage(Message("a", $"m{i}"))));
        var sequences = appended.Select(x => x.Sequence).Order().ToList();

        await Assert.That(String.Join(",", sequences)).IsEqualTo(String.Join(",", Enumerable.Range(1, 20)));
        await Assert.That((await store.GetMessages(new MessageQuery("a"))).Count).IsEqualTo(20);
    }


    [Test]
    public async Task Message_RoundTrips_PolymorphicParts_AndUpdates()
    {
        var (store, _) = this.CreateStores();
        await store.CreateSession(Header("a"));
        var msg = await store.AppendMessage(Message("a", "hi", MessageRole.Assistant) with
        {
            Parts = [new TextPart("hi"), new ToolCallPart("c", "t", "{}"), new ToolResultPart("c", "t", "ok")],
            Status = MessageStatus.Streaming
        });

        await store.UpdateMessage(msg with { Status = MessageStatus.Interrupted, SpokenText = "h", Compacted = true });
        var back = (await store.GetMessages(new MessageQuery("a"))).Single();

        await Assert.That(back.Status).IsEqualTo(MessageStatus.Interrupted);
        await Assert.That(back.SpokenText).IsEqualTo("h");
        await Assert.That(back.Parts[1] is ToolCallPart { Name: "t" }).IsTrue();
        await Assert.That((await store.GetMessages(new MessageQuery("a", IncludeCompacted: false))).Count).IsEqualTo(0);
    }


    [Test]
    public async Task GetMessages_Pages()
    {
        var (store, _) = this.CreateStores();
        await store.CreateSession(Header("a"));
        for (var i = 1; i <= 10; i++)
            await store.AppendMessage(Message("a", i.ToString()));

        string Seq(IReadOnlyList<AiMessageDocument> list) => String.Join(",", list.Select(x => x.Sequence));

        await Assert.That(Seq(await store.GetMessages(new MessageQuery("a", Limit: 3)))).IsEqualTo("8,9,10");
        await Assert.That(Seq(await store.GetMessages(new MessageQuery("a", BeforeSequence: 8, Limit: 3)))).IsEqualTo("5,6,7");
        await Assert.That(Seq(await store.GetMessages(new MessageQuery("a", AfterSequence: 2, Limit: 2)))).IsEqualTo("3,4");
        await Assert.That(Seq(await store.GetMessages(new MessageQuery("a", AfterSequence: 7)))).IsEqualTo("8,9,10");
    }


    [Test]
    public async Task QuerySessions_Filters_Orders_Pages_AndPurges()
    {
        var (store, _) = this.CreateStores();
        var now = DateTimeOffset.UtcNow;
        await store.CreateSession(Header("old", now.AddDays(-10), title: "Old plumbing chat"));
        await store.CreateSession(Header("mid", now.AddDays(-1), new AiSessionSettings { Profile = "cooking", Tags = ["food"] }, summary: "Talked about pasta"));
        await store.CreateSession(Header("new", now));

        string Ids(IReadOnlyList<AiSessionDocument> list) => String.Join(",", list.Select(x => x.SessionId));

        await Assert.That(Ids(await store.QuerySessions(new SessionQuery()))).IsEqualTo("new,mid,old");
        await Assert.That(Ids(await store.QuerySessions(new SessionQuery(Skip: 1, Take: 1)))).IsEqualTo("mid");
        await Assert.That(Ids(await store.QuerySessions(new SessionQuery(Text: "pasta")))).IsEqualTo("mid");
        await Assert.That(Ids(await store.QuerySessions(new SessionQuery(Text: "plumbing")))).IsEqualTo("old");
        await Assert.That(Ids(await store.QuerySessions(new SessionQuery(Profile: "cooking")))).IsEqualTo("mid");
        await Assert.That(Ids(await store.QuerySessions(new SessionQuery(Tags: ["food"])))).IsEqualTo("mid");
        await Assert.That(Ids(await store.QuerySessions(new SessionQuery(From: now.AddDays(-2))))).IsEqualTo("new,mid");

        await store.Purge(now.AddDays(-5));
        await Assert.That(Ids(await store.QuerySessions(new SessionQuery()))).IsEqualTo("new,mid");
    }


    [Test]
    public async Task DeleteSession_CascadesMessages()
    {
        var (store, _) = this.CreateStores();
        await store.CreateSession(Header("a"));
        await store.AppendMessage(Message("a", "hi"));
        await store.DeleteSession("a");

        await Assert.That(await store.GetSession("a")).IsNull();
        await Assert.That((await store.GetMessages(new MessageQuery("a"))).Count).IsEqualTo(0);

        // sequences restart for a re-created id
        await store.CreateSession(Header("a"));
        await Assert.That((await store.AppendMessage(Message("a", "again"))).Sequence).IsEqualTo(1);
    }


    [Test]
    public async Task SearchMessages_FindsRelevant_AndHonoursFilters()
    {
        var (store, _) = this.CreateStores();
        await store.CreateSession(Header("a", title: "Home repairs"));
        await store.CreateSession(Header("b"));
        await store.CreateSession(Header("private", settings: new AiSessionSettings { ExcludeFromSearch = true }));

        var a1 = await store.AppendMessage(Message("a", "The plumber is called Mario"));
        await store.AppendMessage(Message("a", "Lovely weather today", MessageRole.Assistant));
        var a3 = await store.AppendMessage(Message("a", "Mario the plumber comes Tuesday"));
        await store.AppendMessage(Message("b", "Call the plumber about the leak"));
        await store.AppendMessage(Message("private", "plumber Mario secret"));

        var hits = await store.SearchMessages(new MessageSearchQuery("what's the plumber's name, Mario?"));
        await Assert.That(hits.Any(x => x.SessionId == "private")).IsFalse();
        await Assert.That(hits.Any(x => x.Snippet.Contains("weather"))).IsFalse();
        await Assert.That(hits.Count(x => x.SessionId == "a")).IsEqualTo(2);
        await Assert.That(hits.First(x => x.SessionId == "a").SessionTitle).IsEqualTo("Home repairs");

        var others = await store.SearchMessages(new MessageSearchQuery("plumber", ExcludeSessionId: "a"));
        await Assert.That(others.Single().SessionId).IsEqualTo("b");

        var compactedPart = await store.SearchMessages(new MessageSearchQuery("mario", SessionId: "a", ExcludeFromSequence: a3.Sequence));
        await Assert.That(compactedPart.Single().Sequence).IsEqualTo(a1.Sequence);
    }


    [Test]
    public async Task Memory_CreateConflicts_AndStaleUpdateConflicts()
    {
        var (_, store) = this.CreateStores();
        await store.Save(Memory("/a.md"), null);
        await Assert.That(() => store.Save(Memory("/a.md"), null)).Throws<MemoryConflictException>();

        await store.Save(Memory("/a.md", content: "v2", version: 2), 1);
        await Assert.That((await store.Get(MemoryScope.Global, null, "/a.md"))!.Version).IsEqualTo(2);
        await Assert.That(() => store.Save(Memory("/a.md", content: "v3", version: 2), 1)).Throws<MemoryConflictException>();
        await Assert.That(() => store.Save(Memory("/missing.md", version: 2), 1)).Throws<MemoryConflictException>();
    }


    [Test]
    public async Task Memory_List_Clear_AndSize_ByScope()
    {
        var (_, store) = this.CreateStores();
        var now = DateTimeOffset.UtcNow;
        await store.Save(Memory("/g1.md", updated: now.AddMinutes(-2)), null);
        await store.Save(Memory("/notes/g2.md", content: "12345", updated: now), null);
        await store.Save(Memory("/s.md", MemoryScope.Session, "s1"), null);
        await store.Save(Memory("/s.md", MemoryScope.Session, "s2"), null);

        var global = await store.List(MemoryScope.Global);
        await Assert.That(String.Join(",", global.Select(x => x.Path))).IsEqualTo("/notes/g2.md,/g1.md");
        await Assert.That((await store.List(MemoryScope.Global, pathPrefix: "/notes/")).Single().Path).IsEqualTo("/notes/g2.md");
        await Assert.That((await store.List(MemoryScope.Session, "s1")).Count).IsEqualTo(1);
        await Assert.That(await store.GetScopeSize(MemoryScope.Global, null)).IsEqualTo(10);

        await store.Clear(MemoryScope.Session, "s1");
        await Assert.That((await store.List(MemoryScope.Session)).Single().ScopeKey).IsEqualTo("s2");

        await store.Delete(MemoryScope.Global, null, "/g1.md");
        await Assert.That(await store.Get(MemoryScope.Global, null, "/g1.md")).IsNull();
    }
}


[InheritsTests]
public class InMemoryStoreContractTests : StoreContractTests
{
    protected override (IAiSessionStore, IAiMemoryStore) CreateStores()
        => (new InMemoryAiSessionStore(), new InMemoryAiMemoryStore());
}


[InheritsTests]
public class SqliteDocDbStoreContractTests : StoreContractTests
{
    protected override (IAiSessionStore, IAiMemoryStore) CreateStores()
    {
        var store = SqliteTestStore.Create();
        return (new DocumentDbAiSessionStore(store), new DocumentDbAiMemoryStore(store));
    }
}


public static class SqliteTestStore
{
    public static IDocumentStore Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shiny-aiconv-tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Guid.NewGuid():N}.db");

        var opts = new DocumentStoreOptions
        {
            DatabaseProvider = new SqliteDatabaseProvider($"Data Source={path}"),
            UseReflectionFallback = false
        };
        opts.MapAiConversation();
        return new DocumentStore(opts);
    }
}
