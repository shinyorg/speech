namespace Shiny.AiConversation.Tests;

public class InMemoryAiSessionStoreTests
{
    static AiSessionDocument Header(string id, long version = 1, DateTimeOffset? lastActivity = null) => new(
        id, 1, version, null, DateTimeOffset.UtcNow, lastActivity ?? DateTimeOffset.UtcNow,
        new AiSessionSettings(), null, [], SessionUsage.Empty, ContextSnapshot.Empty, 0, 0);

    static AiMessageDocument Message(string sessionId, string text) => new(
        Guid.NewGuid().ToString(), sessionId, 0, 1, DateTimeOffset.UtcNow, MessageRole.User, MessageSource.Typed,
        MessageStatus.Complete, [new TextPart(text)], text, null, null, false, null, "run");


    [Test]
    public async Task AppendMessage_AllocatesIncreasingSequencePerSession()
    {
        var store = new InMemoryAiSessionStore();
        await store.CreateSession(Header("a"));
        await store.CreateSession(Header("b"));

        var a1 = await store.AppendMessage(Message("a", "1"));
        var a2 = await store.AppendMessage(Message("a", "2"));
        var b1 = await store.AppendMessage(Message("b", "1"));

        await Assert.That(a1.Sequence).IsEqualTo(1);
        await Assert.That(a2.Sequence).IsEqualTo(2);
        await Assert.That(b1.Sequence).IsEqualTo(1);
    }


    [Test]
    public async Task SaveSession_RejectsStaleVersion()
    {
        var store = new InMemoryAiSessionStore();
        await store.CreateSession(Header("a"));
        await store.SaveSession(Header("a", 2), expectedVersion: 1);

        await Assert.That(() => store.SaveSession(Header("a", 2), expectedVersion: 1)).Throws<SessionConflictException>();
    }


    [Test]
    public async Task GetMessages_PagesBackwardsFromCursor()
    {
        var store = new InMemoryAiSessionStore();
        await store.CreateSession(Header("a"));
        for (var i = 1; i <= 10; i++)
            await store.AppendMessage(Message("a", i.ToString()));

        var newest = await store.GetMessages(new MessageQuery("a", Limit: 3));
        await Assert.That(String.Join(",", newest.Select(x => x.Sequence))).IsEqualTo("8,9,10");

        var older = await store.GetMessages(new MessageQuery("a", BeforeSequence: 8, Limit: 3));
        await Assert.That(String.Join(",", older.Select(x => x.Sequence))).IsEqualTo("5,6,7");

        var after = await store.GetMessages(new MessageQuery("a", AfterSequence: 2, Limit: 2));
        await Assert.That(String.Join(",", after.Select(x => x.Sequence))).IsEqualTo("3,4");
    }


    [Test]
    public async Task QuerySessions_NewestActivityFirst_AndPurge()
    {
        var store = new InMemoryAiSessionStore();
        var now = DateTimeOffset.UtcNow;
        await store.CreateSession(Header("old", lastActivity: now.AddDays(-10)));
        await store.CreateSession(Header("new", lastActivity: now));

        var list = await store.QuerySessions(new SessionQuery());
        await Assert.That(list[0].SessionId).IsEqualTo("new");

        await store.Purge(now.AddDays(-1));
        list = await store.QuerySessions(new SessionQuery());
        await Assert.That(list.Count).IsEqualTo(1);
    }


    [Test]
    public async Task SearchMessages_RanksByTermsMatched_AndHonoursExcludeFromSearch()
    {
        var store = new InMemoryAiSessionStore();
        await store.CreateSession(Header("a"));
        await store.CreateSession(Header("private") with { Settings = new AiSessionSettings { ExcludeFromSearch = true } });
        await store.AppendMessage(Message("a", "the plumber is called Mario"));
        await store.AppendMessage(Message("a", "the plumber came today"));
        await store.AppendMessage(Message("private", "plumber Mario secret"));

        var hits = await store.SearchMessages(new MessageSearchQuery("plumber mario"));
        await Assert.That(hits.Count).IsEqualTo(2);
        await Assert.That(hits[0].Snippet.Contains("Mario")).IsTrue();
        await Assert.That(hits.All(x => x.SessionId == "a")).IsTrue();
    }
}
