using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AiConversation.Infrastructure;
using static Shiny.AiConversation.Tests.FakeChatClient;

namespace Shiny.AiConversation.Tests;

public class AiSessionManagerTests
{
    [Test]
    public async Task Get_ReturnsANewInstanceEveryTime_RehydratedFromTheStore()   // D9
    {
        var interceptor = new RecordingInterceptor();
        var (manager, _, chat) = TestHelpers.Create(null, interceptor);
        chat.Reply(Text("Which colour?"), AskUser("c1", """[{ "id": "colour", "text": "Which colour?" }]"""));

        string id;
        await using (var session = await manager.Start(new AiSessionSettings { Profile = "paint" }))
        {
            id = session.SessionId;
            await session.Send(AiInput.Text("paint my room")).WaitAsync();
        }

        await using var a = await manager.Get(id);
        await using var b = await manager.Get(id);

        await Assert.That(ReferenceEquals(a, b)).IsFalse();
        await Assert.That(a!.Settings.Profile).IsEqualTo("paint");
        await Assert.That(a.State).IsEqualTo(AiSessionState.AwaitingUser);
        await Assert.That(a.PendingQuestions.Single().Id).IsEqualTo("colour");
        await Assert.That(a.Context.IsEstimate).IsTrue();

        await Assert.That(String.Join(",", interceptor.Loaded.Select(x => x.IsNew))).IsEqualTo("True,False,False");
    }


    [Test]
    public async Task GetLatest_And_Delete()
    {
        var (manager, store, chat) = TestHelpers.Create();
        chat.Reply(Text("ok"));

        await using var first = await manager.Start();
        await Task.Delay(5);
        await using var second = await manager.Start();
        await second.Send(AiInput.Text("hi")).WaitAsync();

        await using var latest = await manager.GetLatest();
        await Assert.That(latest!.SessionId).IsEqualTo(second.SessionId);

        await manager.Delete(second.SessionId);
        await Assert.That(await manager.Get(second.SessionId)).IsNull();
        await Assert.That((await store.GetMessages(new MessageQuery(second.SessionId))).Count).IsEqualTo(0);
    }


    [Test]
    public async Task Load_MarksStreamingMessagesFromDeadRunsAsInterrupted()
    {
        var (manager, store, _) = TestHelpers.Create();
        string id;
        await using (var session = await manager.Start())
            id = session.SessionId;

        AiMessageDocument Msg(string runId, string text) => new(
            Guid.NewGuid().ToString(), id, 0, 1, DateTimeOffset.UtcNow, MessageRole.Assistant, MessageSource.Model,
            MessageStatus.Streaming, [new TextPart(text)], text, null, null, false, null, runId);

        var dead = await store.AppendMessage(Msg("a-dead-process", "half a reply"));
        var live = await store.AppendMessage(Msg(ProcessRun.Id, "still streaming here"));

        await using var loaded = await manager.Get(id);
        var stored = await store.GetMessages(new MessageQuery(id));

        await Assert.That(stored.Single(x => x.MessageId == dead.MessageId).Status).IsEqualTo(MessageStatus.Interrupted);
        await Assert.That(stored.Single(x => x.MessageId == live.MessageId).Status).IsEqualTo(MessageStatus.Streaming);
    }


    [Test]
    public async Task TwoInstances_ReloadWhenTheOtherOneWrote()
    {
        var (manager, _, chat) = TestHelpers.Create();
        chat.Reply(Text("from A"));
        chat.Reply(Text("from B"));
        chat.Reply(Text("A again"));

        await using var a = await manager.Start();
        await using var b = await manager.Get(a.SessionId);

        await a.Send(AiInput.Text("a1")).WaitAsync();
        await b!.Send(AiInput.Text("b1")).WaitAsync();       // b reloads: sees a1
        await a.Send(AiInput.Text("a2")).WaitAsync();        // a reloads: sees b1

        await Assert.That(String.Join("|", chat.Requests[1].Messages.Texts(ChatRole.User))).IsEqualTo("a1|b1");
        await Assert.That(String.Join("|", chat.Requests[2].Messages.Texts(ChatRole.User))).IsEqualTo("a1|b1|a2");
        await Assert.That(String.Join("|", chat.Requests[2].Messages.Texts(ChatRole.Assistant))).IsEqualTo("from A|from B");
    }


    [Test]
    public async Task TwoInstances_ConcurrentTurns_SecondSaveConflicts()
    {
        var (manager, _, chat) = TestHelpers.Create();
        var gate = new TaskCompletionSource();
        chat.ReplyGated(gate, [Text("A is slow")], Text("."));
        chat.Reply(Text("B is fast"));

        await using var a = await manager.Start();
        await using var b = await manager.Get(a.SessionId);

        var turnA = a.Send(AiInput.Text("a"));
        await foreach (var e in turnA.Events)
            if (e is TextDelta) break;

        var resultB = await b!.Send(AiInput.Text("b")).WaitAsync();
        gate.SetResult();
        var resultA = await turnA.WaitAsync();

        await Assert.That(resultB.Outcome).IsEqualTo(AiTurnOutcome.Completed);
        await Assert.That(resultA.Outcome).IsEqualTo(AiTurnOutcome.Failed);
        await Assert.That(resultA.Error is SessionConflictException).IsTrue();
    }


    [Test]
    public async Task SetTitle_And_UpdateSettings_Persist()
    {
        var (manager, store, _) = TestHelpers.Create(1000);
        await using var session = await manager.Start();

        await session.SetTitle("Coffee order");
        await session.UpdateSettings(s => s with { Tags = ["food"], ContextWindow = 2000 });

        var header = await store.GetSession(session.SessionId);
        await Assert.That(header!.Title).IsEqualTo("Coffee order");
        await Assert.That(header.Settings.Tags.Single()).IsEqualTo("food");
        await Assert.That(session.Context.MaxTokens).IsEqualTo(2000);
    }


    [Test]
    public async Task Registration_ResolvesManager_WithDefaultInterceptor_AndProfileTools()
    {
        var chat = new FakeChatClient();
        chat.Reply(Text("ok"));
        chat.Reply(Text("ok"));

        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(chat);
        services.AddShinyAiConversation(o => o
            .AddTool(AIFunctionFactory.Create(() => "x", "everywhere"))
            .AddTool(AIFunctionFactory.Create(() => "y", "cooking_only"), AiToolFlags.Silent, "cooking")
        );
        await using var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IAiSessionManager>();

        await using var plain = await manager.Start();
        await plain.Send(AiInput.Text("hi")).WaitAsync();
        await using var cooking = await manager.Start(new AiSessionSettings { Profile = "cooking" });
        await cooking.Send(AiInput.Text("hi")).WaitAsync();

        string Tools(int i) => String.Join(",", chat.Requests[i].Options!.Tools!.Select(x => x.Name).Order());
        await Assert.That(Tools(0)).IsEqualTo("ask_user,everywhere");
        await Assert.That(Tools(1)).IsEqualTo("ask_user,cooking_only,everywhere");

        // the default interceptor adds the current time to every turn
        await Assert.That(chat.Requests[0].Messages[0].Text.StartsWith("The current time is")).IsTrue();
    }
}
