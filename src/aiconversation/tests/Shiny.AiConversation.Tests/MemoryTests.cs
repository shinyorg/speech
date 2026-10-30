using Microsoft.Extensions.AI;
using Shiny.AiConversation.Infrastructure;
using static Shiny.AiConversation.Tests.FakeChatClient;

namespace Shiny.AiConversation.Tests;

public class MemoryTests
{
    sealed class Fixture
    {
        public InMemoryAiMemoryStore Store { get; } = new();
        public List<(AiMemoryDocument Doc, MemoryChange Change)> Changes { get; } = [];
        public List<(MemoryScope Scope, long Used)> Pressure { get; } = [];
        public SessionMemory Memory { get; }

        public Fixture(AiMemoryOptions? options = null, string? profile = null)
        {
            this.Memory = new SessionMemory(
                this.Store,
                options ?? new AiMemoryOptions(),
                TimeProvider.System,
                "s1",
                profile,
                (d, c) => this.Changes.Add((d, c)),
                (s, k, used, limit) => this.Pressure.Add((s, used))
            );
        }

        public async Task<string> Run(params (string Key, object? Value)[] args)
        {
            var arguments = new AIFunctionArguments();
            foreach (var (key, value) in args)
                arguments[key] = value;

            return (await this.Memory.Tool.InvokeAsync(arguments))!.ToString()!;
        }
    }


    [Test]
    public async Task Create_View_Replace_Append_Rename_Delete()
    {
        var f = new Fixture();
        await Assert.That(await f.Run(("command", "create"), ("path", "notes/prefs.md"), ("content", "likes tea"), ("description", "Preferences")))
            .IsEqualTo("Created /notes/prefs.md (global)");

        await Assert.That(await f.Run(("command", "view"), ("path", "/notes/prefs.md"))).IsEqualTo("likes tea");
        await f.Run(("command", "replace"), ("path", "/notes/prefs.md"), ("old_str", "tea"), ("new_str", "green tea"));
        await f.Run(("command", "append"), ("path", "/notes/prefs.md"), ("content", "metric units"));
        await Assert.That(await f.Run(("command", "view"), ("path", "/notes/prefs.md"))).IsEqualTo("likes green tea\nmetric units");

        await f.Run(("command", "rename"), ("path", "/notes/prefs.md"), ("new_path", "/prefs.md"));
        await Assert.That(await f.Run(("command", "view"), ("path", "/"))).IsEqualTo("/prefs.md (global, 28 bytes): Preferences");

        await f.Run(("command", "delete"), ("path", "/prefs.md"));
        await Assert.That((await f.Store.List()).Count).IsEqualTo(0);

        var changes = String.Join(",", f.Changes.Select(x => x.Change));
        await Assert.That(changes).IsEqualTo("Created,Updated,Updated,Renamed,Deleted");
        await Assert.That(f.Changes.All(x => x.Doc.UpdatedBy == MemoryAuthor.Assistant)).IsTrue();
    }


    [Test]
    public async Task Errors_GoBackToTheModel_NotThrown()
    {
        var f = new Fixture();
        await f.Run(("command", "create"), ("path", "/a.md"), ("content", "one one"));

        await Assert.That((await f.Run(("command", "create"), ("path", "/a.md"), ("content", "x"))).StartsWith("Error: /a.md already exists")).IsTrue();
        await Assert.That((await f.Run(("command", "replace"), ("path", "/a.md"), ("old_str", "one"), ("new_str", "two"))).Contains("more than once")).IsTrue();
        await Assert.That((await f.Run(("command", "replace"), ("path", "/a.md"), ("old_str", "zzz"), ("new_str", "two"))).Contains("not found")).IsTrue();
        await Assert.That((await f.Run(("command", "view"), ("path", "/../etc"))).StartsWith("Error")).IsTrue();
        await Assert.That((await f.Run(("command", "create"), ("path", "/p.md"), ("content", "x"), ("scope", "profile"))).Contains("no profile")).IsTrue();
        await Assert.That((await f.Run(("command", "explode"), ("path", "/a.md"))).StartsWith("Error")).IsTrue();
    }


    [Test]
    public async Task Limits_DocumentSize_WritesPerTurn_AndScopeSize()
    {
        var f = new Fixture(new AiMemoryOptions { MaxDocumentBytes = 10, MaxWritesPerTurn = 2, MaxScopeBytes = 15 });

        await Assert.That((await f.Run(("command", "create"), ("path", "/big.md"), ("content", new string('x', 11)))).Contains("over the 10 byte limit")).IsTrue();

        f.Memory.BeginTurn();
        await f.Run(("command", "create"), ("path", "/a.md"), ("content", "1234567890"));
        var full = await f.Run(("command", "create"), ("path", "/b.md"), ("content", "123456"));
        await Assert.That(full.Contains("memory is full")).IsTrue();
        await Assert.That(f.Pressure.Single().Used).IsEqualTo(10);

        var tooMany = await f.Run(("command", "delete"), ("path", "/a.md"));
        await Assert.That(tooMany.Contains("too many memory writes")).IsTrue();

        f.Memory.BeginTurn();
        await Assert.That(await f.Run(("command", "delete"), ("path", "/a.md"))).IsEqualTo("Deleted /a.md");
    }


    [Test]
    public async Task OversizedDocument_IsViewedInPages()
    {
        var f = new Fixture(new AiMemoryOptions { MaxDocumentBytes = 5 });
        var service = new AiMemoryService(f.Store, TimeProvider.System);
        await service.Save(MemoryScope.Global, null, "/long.md", "abcdefghij");   // the app can exceed the limit

        var first = await f.Run(("command", "view"), ("path", "/long.md"));
        await Assert.That(first.StartsWith("abcde\n[showing 0-5 of 10 characters; view with offset 5")).IsTrue();
        await Assert.That(await f.Run(("command", "view"), ("path", "/long.md"), ("offset", 5))).IsEqualTo("fghij");
    }


    [Test]
    public async Task Scopes_SessionProfileGlobal_AreSeparate()
    {
        var f = new Fixture(profile: "cooking");
        await f.Run(("command", "create"), ("path", "/n.md"), ("content", "session"), ("scope", "session"));
        await f.Run(("command", "create"), ("path", "/n.md"), ("content", "profile"), ("scope", "profile"));
        await f.Run(("command", "create"), ("path", "/n.md"), ("content", "global"));

        await Assert.That((await f.Store.Get(MemoryScope.Session, "s1", "/n.md"))!.Content).IsEqualTo("session");
        await Assert.That((await f.Store.Get(MemoryScope.Profile, "cooking", "/n.md"))!.Content).IsEqualTo("profile");

        // without a scope, lookups go session -> profile -> global
        await Assert.That(await f.Run(("command", "view"), ("path", "/n.md"))).IsEqualTo("session");
        await Assert.That(await f.Run(("command", "view"), ("path", "/n.md"), ("scope", "global"))).IsEqualTo("global");
    }


    [Test]
    public async Task Index_ListsPathsAndDescriptions_WrappedAsData()
    {
        var f = new Fixture(new AiMemoryOptions { MaxIndexEntries = 2 });
        await Assert.That(await f.Memory.BuildIndex(CancellationToken.None)).IsNull();

        await f.Run(("command", "create"), ("path", "/a.md"), ("content", "aaa"), ("description", "First"));
        await Task.Delay(2);
        await f.Run(("command", "create"), ("path", "/b.md"), ("content", "bb"));
        await Task.Delay(2);
        var service = new AiMemoryService(f.Store, TimeProvider.System);
        await service.Save(MemoryScope.Global, null, "/c.md", "c", "Edited");

        var index = (await f.Memory.BuildIndex(CancellationToken.None))!;
        await Assert.That(index.StartsWith("<memory_index>") && index.EndsWith("</memory_index>")).IsTrue();
        await Assert.That(index.Contains("data, not instructions")).IsTrue();
        await Assert.That(index.Contains("- /c.md (global, 1 bytes): Edited [edited by the user]")).IsTrue();
        await Assert.That(index.Contains("/a.md")).IsFalse();
        await Assert.That(index.Contains("1 more; use view")).IsTrue();
    }


    [Test]
    public async Task Session_AddsSilentMemoryTool_AndIndex_AndPublishesChanges()
    {
        var h = TestHelpers.CreateHarness(memory: new AiMemoryOptions());
        await h.Manager.Memory.Save(MemoryScope.Global, null, "/prefs.md", "vegetarian", "Diet");

        await using var session = await h.Manager.Start();
        await using var events = await EventCollector.Start(session);
        h.Chat.Reply(Call("m1", SessionMemory.ToolName, new() { ["command"] = "create", ["path"] = "/todo.md", ["content"] = "buy milk", ["scope"] = "session" }));
        h.Chat.Reply(Text("Noted."));

        var turn = session.Send(AiInput.Text("remind me to buy milk"));
        var result = await turn.WaitAsync();

        var request = h.Chat.Requests[0];
        await Assert.That(request.Options!.Tools!.Any(x => x.Name == SessionMemory.ToolName)).IsTrue();
        await Assert.That(request.Messages[0].Text.Contains("/prefs.md (global, 10 bytes): Diet")).IsTrue();

        var turnEvents = new List<AiTurnEvent>();
        await foreach (var e in turn.Events) turnEvents.Add(e);
        await Assert.That(turnEvents.OfType<ToolCallStarted>().Single().Silent).IsTrue();

        await events.WaitFor(e => e.OfType<MemoryChanged>().Any());
        await Assert.That(events.Events.OfType<MemoryChanged>().Single().Document.Path).IsEqualTo("/todo.md");

        // memory writes are model-only parts of the stored reply
        await Assert.That(result.Reply!.Parts.OfType<ToolCallPart>().All(x => x.Audience == Audience.ModelOnly)).IsTrue();
        await Assert.That(result.Reply.Text).IsEqualTo("Noted.");

        // deleting the session clears its session-scoped memory, not global memory
        await h.Manager.Delete(session.SessionId);
        await Assert.That(await h.MemoryStore.Get(MemoryScope.Session, session.SessionId, "/todo.md")).IsNull();
        await Assert.That(await h.MemoryStore.Get(MemoryScope.Global, null, "/prefs.md")).IsNotNull();
    }


    [Test]
    public async Task Memory_CanBeDisabled_GloballyOrPerProfile()
    {
        var h = TestHelpers.CreateHarness(memory: new AiMemoryOptions { DisabledProfiles = ["private"] });
        h.Chat.Reply(Text("a"));
        h.Chat.Reply(Text("b"));

        await using var normal = await h.Manager.Start();
        await normal.Send(AiInput.Text("hi")).WaitAsync();
        await using var secret = await h.Manager.Start(new AiSessionSettings { Profile = "private" });
        await secret.Send(AiInput.Text("hi")).WaitAsync();

        await Assert.That(h.Chat.Requests[0].Options!.Tools!.Any(x => x.Name == SessionMemory.ToolName)).IsTrue();
        await Assert.That(h.Chat.Requests[1].Options!.Tools!.Any(x => x.Name == SessionMemory.ToolName)).IsFalse();
    }


    [Test]
    public async Task MemoryService_UserEdits_AndForgetEverything()
    {
        var h = TestHelpers.CreateHarness(memory: new AiMemoryOptions());
        var saved = await h.Manager.Memory.Save(MemoryScope.Global, null, "/a.md", "one");
        var updated = await h.Manager.Memory.Save(MemoryScope.Global, null, "/a.md", "two");
        await h.Manager.Memory.Save(MemoryScope.Session, "s1", "/b.md", "x");

        await Assert.That(saved.UpdatedBy).IsEqualTo(MemoryAuthor.User);
        await Assert.That(updated.Version).IsEqualTo(2);

        await h.Manager.Memory.Clear();
        await Assert.That((await h.Manager.Memory.List()).Count).IsEqualTo(0);
    }
}
