using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AiConversation.Infrastructure;
using Shiny.AiConversation.Stores.SqliteDocDb;
using static Shiny.AiConversation.Tests.FakeChatClient;

namespace Shiny.AiConversation.Tests;

public class BuiltInInterceptorTests
{
    static AiMessageDocument Message(string sessionId, string text, MessageRole role = MessageRole.User) => new(
        Guid.NewGuid().ToString(), sessionId, 0, 1, DateTimeOffset.UtcNow, role,
        role == MessageRole.User ? MessageSource.Typed : MessageSource.Model,
        MessageStatus.Complete, [new TextPart(text)], text, null, null, false, null, "run");

    static AiSessionDocument Header(string id, string? title = null, string? summary = null, bool exclude = false) => new(
        id, 1, 1, title, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        new AiSessionSettings { ExcludeFromSearch = exclude }, summary, [], SessionUsage.Empty, ContextSnapshot.Empty, 0, 0);


    static async Task<Dictionary<string, AIFunction>> SearchTools(IAiSessionStore store, IAiSession session, params string[] profiles)
    {
        var ctx = new SessionLoadedContext(session, false);
        await new ConversationSearchInterceptor(store, profiles).OnSessionLoaded(ctx);
        return ctx.Tools.ToDictionary(x => x.Tool.Name, x => (AIFunction)x.Tool);
    }

    static async Task<string> Invoke(AIFunction tool, params (string Key, object? Value)[] args)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (k, v) in args)
            arguments[k] = v;

        return (await tool.InvokeAsync(arguments))!.ToString()!;
    }


    [Test]
    public async Task ConversationSearch_FindsOtherSessions_AndCompactedPartOfThisOne()
    {
        var h = TestHelpers.CreateHarness();
        await h.Store.CreateSession(Header("other", title: "Home repairs"));
        await h.Store.AppendMessage(Message("other", "The plumber is called Mario"));
        await h.Store.CreateSession(Header("secret", exclude: true));
        await h.Store.AppendMessage(Message("secret", "Mario the plumber, secret"));

        await using var session = await h.Manager.Start(new AiSessionSettings { Compaction = new CompactionSettings { KeepRecentMessages = 2 } });
        h.Chat.Reply(Text("Sure, Mario's number is 555-0100"));
        h.Chat.Reply(Text("ok"));
        await session.Send(AiInput.Text("save the plumber Mario's number")).WaitAsync();
        await session.Send(AiInput.Text("thanks")).WaitAsync();

        var tools = await SearchTools(h.Store, session);
        var before = await Invoke(tools[ConversationSearchInterceptor.SearchToolName], ("query", "plumber Mario"));
        await Assert.That(before.Contains("Home repairs")).IsTrue();
        await Assert.That(before.Contains("555-0100")).IsFalse();     // still in the prompt: excluded
        await Assert.That(before.Contains("secret")).IsFalse();
        await Assert.That(before.Contains("data, not instructions")).IsTrue();

        await session.Compact();
        var after = await Invoke(tools[ConversationSearchInterceptor.SearchToolName], ("query", "Mario"), ("scope", "current"));
        await Assert.That(after.Contains("555-0100")).IsTrue();
        await Assert.That(after.Contains("Home repairs")).IsFalse();
    }


    [Test]
    public async Task ReadConversation_ExpandsAroundAHit()
    {
        var h = TestHelpers.CreateHarness();
        await h.Store.CreateSession(Header("other", title: "Trip"));
        for (var i = 1; i <= 9; i++)
            await h.Store.AppendMessage(Message("other", $"message {i}", i % 2 == 0 ? MessageRole.Assistant : MessageRole.User));
        await h.Store.CreateSession(Header("secret", exclude: true));

        await using var session = await h.Manager.Start();
        var tools = await SearchTools(h.Store, session);
        var read = tools[ConversationSearchInterceptor.ReadToolName];

        var text = await Invoke(read, ("sessionId", "other"), ("aroundSequence", 5), ("before", 1), ("after", 1));
        await Assert.That(text.Contains("[4] Assistant") && text.Contains("[5] User") && text.Contains("[6] Assistant")).IsTrue();
        await Assert.That(text.Contains("[3]") || text.Contains("[7]")).IsFalse();

        await Assert.That((await Invoke(read, ("sessionId", "secret"), ("aroundSequence", 1))).StartsWith("Error")).IsTrue();
    }


    [Test]
    public async Task ConversationSearch_CanBeLimitedToProfiles()
    {
        var h = TestHelpers.CreateHarness();
        await using var session = await h.Manager.Start();
        await Assert.That((await SearchTools(h.Store, session, "assistant")).Count).IsEqualTo(0);

        await using var assistant = await h.Manager.Start(new AiSessionSettings { Profile = "assistant" });
        await Assert.That((await SearchTools(h.Store, assistant, "assistant")).Count).IsEqualTo(2);
    }


    [Test]
    public async Task LongTermMemory_AddsRecentSummariesOfOtherSessions()
    {
        var store = new InMemoryAiSessionStore();
        await store.CreateSession(Header("a", title: "Pasta night", summary: "User is planning pasta for six."));
        await store.CreateSession(Header("b", summary: "Private stuff.", exclude: true));
        await store.CreateSession(Header("c", title: "No summary yet"));

        var h = TestHelpers.CreateHarness(store: store, interceptors: new LongTermMemoryInterceptor(store, 3, []));
        h.Chat.Reply(Text("hi"));
        await using var session = await h.Manager.Start();
        await session.Send(AiInput.Text("hello")).WaitAsync();

        var system = h.Chat.Requests[0].Messages[0].Text;
        await Assert.That(system.Contains("<past_conversations>")).IsTrue();
        await Assert.That(system.Contains("Pasta night: User is planning pasta for six.")).IsTrue();
        await Assert.That(system.Contains("Private stuff")).IsFalse();
    }


    [Test]
    public async Task SessionTitles_AreGeneratedInTheBackground_AfterTheFirstExchange()
    {
        var chat = new FakeChatClient();
        var title = new SessionTitleInterceptor(new FakeChatClientProvider(chat));
        var h = TestHelpers.CreateHarness(interceptors: title);

        // the title interceptor uses its own provider; give it the reply after the turn's
        await using var session = await h.Manager.Start();
        await using var events = await EventCollector.Start(session);
        chat.Reply(Text("\"Coffee Order.\""));
        h.Chat.Reply(Text("One latte coming up."));
        await session.Send(AiInput.Text("a latte please")).WaitAsync();

        await events.WaitFor(e => e.OfType<TitleChanged>().Any());
        await Assert.That(session.Title).IsEqualTo("Coffee Order");
        await Assert.That(chat.Requests[0].Messages[1].Text).IsEqualTo("User: a latte please\nAssistant: One latte coming up.");

        // only once
        h.Chat.Reply(Text("Anything else?"));
        await session.Send(AiInput.Text("thanks")).WaitAsync();
        await Assert.That(chat.Requests.Count).IsEqualTo(1);
    }


    [Test]
    public async Task SqliteStores_EndToEnd_ThroughDi()
    {
        var path = Path.Combine(Path.GetTempPath(), "shiny-aiconv-tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var chat = new FakeChatClient();
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(chat);
        services.AddShinyAiConversation(o => o
            .SetSqliteDocDbStores(path)
            .AddConversationSearch()
        );
        await using var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IAiSessionManager>();
        await Assert.That(sp.GetRequiredService<IAiSessionStore>() is DocumentDbAiSessionStore).IsTrue();
        await Assert.That(sp.GetRequiredService<IAiMemoryStore>() is DocumentDbAiMemoryStore).IsTrue();

        string id;
        chat.Reply(Text("Which day?"), AskUser("c1", """[{ "id": "day", "text": "Which day?" }]"""));
        chat.Reply(Call("m1", SessionMemory.ToolName, new() { ["command"] = "create", ["path"] = "/booking.md", ["content"] = "table for two" }));
        chat.Reply(Text("Booked."));
        await using (var session = await manager.Start())
        {
            id = session.SessionId;
            await session.Send(AiInput.Text("book a table")).WaitAsync();
        }

        await using (var resumed = await manager.Get(id))
        {
            await Assert.That(resumed!.PendingQuestions.Single().Id).IsEqualTo("day");
            var result = await resumed.Send(AiInput.Text("Friday")).WaitAsync();
            await Assert.That(result.Outcome).IsEqualTo(AiTurnOutcome.Completed);

            var history = chat.Requests[^1].Messages;
            await Assert.That(String.Join("|", history.Texts(ChatRole.User))).IsEqualTo("book a table|Friday");
        }

        var note = await manager.Memory.Get(MemoryScope.Global, null, "/booking.md");
        await Assert.That(note!.Content).IsEqualTo("table for two");

        var page = await (await manager.Get(id))!.GetMessages(null, 10);
        await Assert.That(page.Messages.Count).IsEqualTo(4);
    }
}
