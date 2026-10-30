using Microsoft.Extensions.AI;
using Shiny.AiConversation.Infrastructure;
using static Shiny.AiConversation.Tests.FakeChatClient;

namespace Shiny.AiConversation.Tests;

public class AiSessionTests
{
    [Test]
    public async Task Send_StreamsReply_AndStoresBothSides()
    {
        var (manager, store, chat) = TestHelpers.Create();
        chat.Reply(Text("Hello "), Text("there."));

        await using var session = await manager.Start();
        var turn = session.Send(AiInput.Text("Hi"));

        var deltas = new List<string>();
        await foreach (var e in turn.Events)
        {
            if (e is TextDelta d)
                deltas.Add(d.Text);
        }
        var result = await turn.WaitAsync();

        await Assert.That(String.Concat(deltas)).IsEqualTo("Hello there.");
        await Assert.That(result.Outcome).IsEqualTo(AiTurnOutcome.Completed);
        await Assert.That(result.Reply!.Text).IsEqualTo("Hello there.");
        await Assert.That(session.State).IsEqualTo(AiSessionState.Idle);

        var stored = await store.GetMessages(new MessageQuery(session.SessionId));
        await Assert.That(stored.Count).IsEqualTo(2);
        await Assert.That(stored[0].Role).IsEqualTo(MessageRole.User);
        await Assert.That(stored[1].Status).IsEqualTo(MessageStatus.Complete);
    }


    [Test]
    public async Task TurnEvents_ReplayOnReEnumeration()
    {
        var (manager, _, chat) = TestHelpers.Create();
        chat.Reply(Text("Hi"));

        await using var session = await manager.Start();
        var turn = session.Send(AiInput.Text("Hi"));
        await turn.WaitAsync();

        var first = new List<AiTurnEvent>();
        await foreach (var e in turn.Events) first.Add(e);
        var second = new List<AiTurnEvent>();
        await foreach (var e in turn.Events) second.Add(e);

        await Assert.That(first.Count).IsEqualTo(second.Count);
        await Assert.That(first[0] is TurnStarted).IsTrue();
        await Assert.That(first[^1] is TurnCompleted).IsTrue();
    }


    [Test]
    public async Task AskUser_EndsTurnAwaitingUser_WithStructuredQuestions()
    {
        var (manager, store, chat) = TestHelpers.Create();
        chat.Reply(
            Text("What size would you like?"),
            AskUser("c1", """[{ "id": "size", "text": "What size?", "choices": [{ "id": "s", "label": "Small" }, { "id": "l", "label": "Large" }] }]""")
        );

        await using var session = await manager.Start();
        var result = await session.Send(AiInput.Text("I want a coffee")).WaitAsync();

        await Assert.That(chat.Requests.Count).IsEqualTo(1);          // terminating: no second round trip
        await Assert.That(result.Outcome).IsEqualTo(AiTurnOutcome.AwaitingUser);
        await Assert.That(session.State).IsEqualTo(AiSessionState.AwaitingUser);
        await Assert.That(session.PendingQuestions.Single().Choices!.Count).IsEqualTo(2);

        // the call and its result are persisted (P6), the display text has no protocol noise (L12)
        await Assert.That(result.Reply!.Parts.OfType<ToolCallPart>().Single().Name).IsEqualTo(AskUserFunction.ToolName);
        await Assert.That(result.Reply.Text).IsEqualTo("What size would you like?");

        var header = await store.GetSession(session.SessionId);
        await Assert.That(header!.PendingQuestions.Single().Id).IsEqualTo("size");
    }


    [Test]
    public async Task AskUser_Voice_IsLimitedToOneQuestion()   // L7
    {
        var (manager, _, chat) = TestHelpers.Create();
        chat.Reply(AskUser("c1", """[{ "id": "a", "text": "First?" }, { "id": "b", "text": "Second?" }]"""));

        await using var session = await manager.Start();
        await session.Send(AiInput.Voice("book a table")).WaitAsync();

        await Assert.That(session.PendingQuestions.Count).IsEqualTo(1);

        var request = chat.Requests[0];
        var askTool = request.Options!.Tools!.Single(x => x.Name == AskUserFunction.ToolName);
        await Assert.That(askTool.Description.Contains("at most one question")).IsTrue();
        await Assert.That(request.Messages[0].Text.Contains("spoken aloud")).IsTrue();
    }


    [Test]
    public async Task ReplyEndingInQuestion_StillAwaitsUser()   // L8
    {
        var (manager, _, chat) = TestHelpers.Create();
        chat.Reply(Text("Sure. Would you like it hot or iced?"));

        await using var session = await manager.Start();
        var result = await session.Send(AiInput.Text("coffee")).WaitAsync();

        await Assert.That(result.Outcome).IsEqualTo(AiTurnOutcome.AwaitingUser);
        await Assert.That(session.PendingQuestions.Single().Text).IsEqualTo("Would you like it hot or iced?");
        await Assert.That(session.PendingQuestions.Single().HasChoices).IsFalse();
    }


    [Test]
    public async Task EachTurn_ReplacesPendingQuestions()   // L6
    {
        var (manager, store, chat) = TestHelpers.Create();
        chat.Reply(Text("Which day?"), AskUser("c1", """[{ "id": "day", "text": "Which day?" }]"""));
        chat.Reply(Text("Booked for Tuesday."));

        await using var session = await manager.Start();
        await session.Send(AiInput.Text("book it")).WaitAsync();
        await Assert.That(session.PendingQuestions.Count).IsEqualTo(1);

        await session.Send(AiInput.Text("Tuesday")).WaitAsync();
        await Assert.That(session.PendingQuestions.Count).IsEqualTo(0);
        await Assert.That(session.State).IsEqualTo(AiSessionState.Idle);
        await Assert.That((await store.GetSession(session.SessionId))!.PendingQuestions.Count).IsEqualTo(0);

        // the ask_user call and its result are replayed to the model as a valid tool exchange
        var history = chat.Requests[1].Messages;
        await Assert.That(history.Any(m => m.Role == ChatRole.Tool)).IsTrue();
    }


    [Test]
    public async Task UserMessage_IsStoredBeforeTheModelCall_EvenWhenItFails()   // P6, L9
    {
        var (manager, store, chat) = TestHelpers.Create();
        chat.Throw(new HttpRequestException("boom"));

        await using var session = await manager.Start();
        await using var events = await EventCollector.Start(session);
        var result = await session.Send(AiInput.Text("are you there?")).WaitAsync();

        await Assert.That(result.Outcome).IsEqualTo(AiTurnOutcome.Failed);
        await Assert.That(result.Error is HttpRequestException).IsTrue();

        var stored = await store.GetMessages(new MessageQuery(session.SessionId));
        await Assert.That(stored[0].Text).IsEqualTo("are you there?");
        await Assert.That(stored[1].Status).IsEqualTo(MessageStatus.Failed);

        await events.WaitFor(e => e.OfType<SessionError>().Any());
        await Assert.That(session.State).IsEqualTo(AiSessionState.Idle);
    }


    [Test]
    public async Task Send_WhileActive_SupersedesTheActiveTurn()   // P3
    {
        var (manager, _, chat) = TestHelpers.Create();
        var gate = new TaskCompletionSource();
        chat.ReplyGated(gate, [Text("Long answer...")], Text(" never sent"));
        chat.Reply(Text("Second answer."));

        await using var session = await manager.Start();
        var first = session.Send(AiInput.Text("first"));
        await WaitForText(first);

        var second = session.Send(AiInput.Text("second"));
        var firstResult = await first.WaitAsync();
        var secondResult = await second.WaitAsync();

        await Assert.That(firstResult.Outcome).IsEqualTo(AiTurnOutcome.Superseded);
        await Assert.That(firstResult.Reply!.Status).IsEqualTo(MessageStatus.Interrupted);
        await Assert.That(firstResult.Reply.Text).IsEqualTo("Long answer...");
        await Assert.That(secondResult.Outcome).IsEqualTo(AiTurnOutcome.Completed);
        await Assert.That(session.ActiveTurn).IsNull();

        // the second request sees the first exchange, with the partial reply marked as interrupted
        var history = chat.Requests[1].Messages;
        await Assert.That(String.Join("|", history.Texts(ChatRole.User))).IsEqualTo("first|second");
        await Assert.That(history.Texts(ChatRole.Assistant).Single()).IsEqualTo($"Long answer... {MessageMapper.InterruptedMarker}");
    }


    [Test]
    public async Task Interrupt_WhileStreaming_RecordsWhatWasHeard()   // P4
    {
        var (manager, _, chat) = TestHelpers.Create();
        var gate = new TaskCompletionSource();
        chat.ReplyGated(gate, [Text("Here is a very long story about")], Text(" dragons."));
        chat.Reply(Text("OK."));

        await using var session = await manager.Start();
        var turn = session.Send(AiInput.Voice("tell me a story"));
        await WaitForText(turn);

        turn.Interrupt("Here is a very");
        var result = await turn.WaitAsync();

        await Assert.That(result.Outcome).IsEqualTo(AiTurnOutcome.Interrupted);
        await Assert.That(result.Reply!.SpokenText).IsEqualTo("Here is a very");

        var events = new List<AiTurnEvent>();
        await foreach (var e in turn.Events) events.Add(e);
        await Assert.That(events[^1] is TurnInterrupted { SpokenText: "Here is a very" }).IsTrue();

        await session.Send(AiInput.Voice("stop")).WaitAsync();
        var assistant = chat.Requests[1].Messages.Texts(ChatRole.Assistant).Single();
        await Assert.That(assistant).IsEqualTo($"Here is a very {MessageMapper.InterruptedMarker}");
    }


    [Test]
    public async Task Interrupt_AfterCompletion_MarksTheStoredReply()
    {
        var (manager, store, chat) = TestHelpers.Create();
        chat.Reply(Text("A finished reply that was still being spoken."));
        chat.Reply(Text("Sure."));

        await using var session = await manager.Start();
        var turn = session.Send(AiInput.Voice("hi"));
        var result = await turn.WaitAsync();

        turn.Interrupt("A finished reply");
        await session.Send(AiInput.Voice("wait")).WaitAsync();

        var stored = (await store.GetMessages(new MessageQuery(session.SessionId))).Single(x => x.MessageId == result.Reply!.MessageId);
        await Assert.That(stored.Status).IsEqualTo(MessageStatus.Interrupted);
        await Assert.That(stored.SpokenText).IsEqualTo("A finished reply");
        await Assert.That(chat.Requests[1].Messages.Texts(ChatRole.Assistant).Single()).IsEqualTo($"A finished reply {MessageMapper.InterruptedMarker}");
    }


    [Test]
    public async Task CallerCancellation_IsNotAnError()   // L9
    {
        var (manager, _, chat) = TestHelpers.Create();
        var gate = new TaskCompletionSource();
        chat.ReplyGated(gate, [Text("Thinking about it")]);

        await using var session = await manager.Start();
        await using var events = await EventCollector.Start(session);
        using var cts = new CancellationTokenSource();
        var turn = session.Send(AiInput.Text("hi"), cts.Token);
        await WaitForText(turn);
        cts.Cancel();

        var result = await turn.WaitAsync();
        await Assert.That(result.Outcome).IsEqualTo(AiTurnOutcome.Interrupted);
        await Assert.That(result.Error).IsNull();
        await Assert.That(events.Events.OfType<SessionError>().Any()).IsFalse();
    }


    [Test]
    public async Task FailingInterceptor_DoesNotFailTheTurn()   // L9
    {
        var interceptor = new RecordingInterceptor { OnBefore = _ => throw new InvalidOperationException("bad interceptor") };
        var (manager, _, chat) = TestHelpers.Create(null, interceptor);
        chat.Reply(Text("Fine."));

        await using var session = await manager.Start();
        var result = await session.Send(AiInput.Text("hi")).WaitAsync();

        await Assert.That(result.Outcome).IsEqualTo(AiTurnOutcome.Completed);
        await Assert.That(interceptor.After.Count).IsEqualTo(1);
    }


    [Test]
    public async Task ToolLoop_UsageIsCumulative_ContextIsLastRequest()
    {
        var weather = AIFunctionFactory.Create(() => "sunny", "weather");
        var interceptor = new RecordingInterceptor { OnLoaded = ctx => ctx.AddTool(weather) };
        var (manager, _, chat) = TestHelpers.Create(1000, interceptor);
        chat.Reply(Text("Checking."), Call("c1", "weather"), Usage(500, 20));
        chat.Reply(Text("It's sunny."), Usage(560, 10));

        await using var session = await manager.Start();
        await using var events = await EventCollector.Start(session);
        var result = await session.Send(AiInput.Text("weather?")).WaitAsync();

        await Assert.That(session.Usage.InputTokens).IsEqualTo(1060);
        await Assert.That(session.Usage.OutputTokens).IsEqualTo(30);
        await Assert.That(session.Usage.RequestCount).IsEqualTo(2);
        await Assert.That(result.Usage!.InputTokens).IsEqualTo(1060);

        await Assert.That(session.Context.CurrentTokens).IsEqualTo(570);
        await Assert.That(session.Context.IsEstimate).IsFalse();
        await Assert.That(session.Context.Pressure).IsEqualTo(ContextPressure.Normal);

        var turnEvents = events.Events.OfType<TurnEventOccurred>().Select(x => x.Event).ToList();
        await Assert.That(turnEvents.OfType<ToolCallStarted>().Single(x => x.Name == "weather").Silent).IsFalse();
        await Assert.That(turnEvents.OfType<ToolCallCompleted>().Single().Result).IsEqualTo("sunny");
        await Assert.That(result.Reply!.Text).IsEqualTo("Checking.\n\nIt's sunny.");
    }


    [Test]
    public async Task ContextPressure_ChangesAtThresholds()
    {
        var (manager, _, chat) = TestHelpers.Create(1000);
        chat.Reply(Text("a"), Usage(500, 10));
        chat.Reply(Text("b"), Usage(800, 10));
        chat.Reply(Text("c"), Usage(950, 10));

        await using var session = await manager.Start();
        await using var events = await EventCollector.Start(session);

        await session.Send(AiInput.Text("1")).WaitAsync();
        await Assert.That(session.Context.Pressure).IsEqualTo(ContextPressure.Normal);
        await session.Send(AiInput.Text("2")).WaitAsync();
        await Assert.That(session.Context.Pressure).IsEqualTo(ContextPressure.Warning);
        await session.Send(AiInput.Text("3")).WaitAsync();
        await Assert.That(session.Context.Pressure).IsEqualTo(ContextPressure.Critical);

        await events.WaitFor(e => e.OfType<ContextPressureChanged>().Count() >= 2);
        var changes = events.Events.OfType<ContextPressureChanged>().Select(x => x.Context.Pressure).ToList();
        await Assert.That(changes.Contains(ContextPressure.Warning) && changes.Contains(ContextPressure.Critical)).IsTrue();
    }


    [Test]
    public async Task NoUsageReported_ContextIsEstimated_WithoutPressure()
    {
        var (manager, _, chat) = TestHelpers.Create();
        chat.Reply(Text("A reply with no usage reported."));

        await using var session = await manager.Start();
        await session.Send(AiInput.Text("hello")).WaitAsync();

        await Assert.That(session.Context.IsEstimate).IsTrue();
        await Assert.That(session.Context.CurrentTokens > 0).IsTrue();
        await Assert.That(session.Context.Pressure).IsNull();
        await Assert.That(session.Usage.RequestCount).IsEqualTo(1);
    }


    [Test]
    public async Task SessionEvents_StartWithSnapshot_InOrder()
    {
        var (manager, _, chat) = TestHelpers.Create();
        chat.Reply(Text("Hi!"));

        await using var session = await manager.Start();
        await using var events = await EventCollector.Start(session);
        await session.Send(AiInput.Text("hello")).WaitAsync();
        await events.WaitFor(e => e.OfType<TurnEventOccurred>().Any(x => x.Event is TurnCompleted));

        var list = events.Events;
        await Assert.That(list[0] is Snapshot).IsTrue();
        await Assert.That(list.OfType<MessageAdded>().Count()).IsEqualTo(2);
        await Assert.That(list.OfType<MessageUpdated>().Count()).IsEqualTo(1);

        var states = list.OfType<StateChanged>().Select(x => x.State).ToList();
        await Assert.That(String.Join(",", states)).IsEqualTo("Thinking,Responding,Idle");
    }


    [Test]
    public async Task Prompt_HasInterceptorPromptsFirst_ThenHistory()
    {
        var interceptor = new RecordingInterceptor
        {
            OnLoaded = ctx => ctx.SystemPrompts.Add("You are a barista."),
            OnBefore = ctx => ctx.SystemPrompts.Add("It is morning.")
        };
        var (manager, _, chat) = TestHelpers.Create(null, interceptor);
        chat.Reply(Text("One."));
        chat.Reply(Text("Two."));

        await using var session = await manager.Start();
        await session.Send(AiInput.Text("first")).WaitAsync();
        await session.Send(AiInput.Text("second")).WaitAsync();

        var messages = chat.Requests[1].Messages;
        await Assert.That(messages[0].Role).IsEqualTo(ChatRole.System);
        await Assert.That(messages[0].Text).IsEqualTo("You are a barista.\n\nIt is morning.");
        await Assert.That(String.Join("|", messages.Skip(1).Select(m => $"{m.Role}:{m.Text}"))).IsEqualTo("user:first|assistant:One.|user:second");
    }


    [Test]
    public async Task GetMessages_PagesNewestFirst()
    {
        var (manager, _, chat) = TestHelpers.Create();
        for (var i = 0; i < 3; i++)
            chat.Reply(Text($"reply {i}"));

        await using var session = await manager.Start();
        for (var i = 0; i < 3; i++)
            await session.Send(AiInput.Text($"msg {i}")).WaitAsync();

        var page = await session.GetMessages(null, 4);
        await Assert.That(page.HasMore).IsTrue();
        await Assert.That(page.Messages[0].Text).IsEqualTo("msg 1");

        var older = await session.GetMessages(page.Messages[0].Sequence, 4);
        await Assert.That(older.HasMore).IsFalse();
        await Assert.That(older.Messages.Count).IsEqualTo(2);
    }


    static async Task WaitForText(IAiTurn turn)
    {
        await foreach (var e in turn.Events)
        {
            if (e is TextDelta)
                return;
        }
    }
}
