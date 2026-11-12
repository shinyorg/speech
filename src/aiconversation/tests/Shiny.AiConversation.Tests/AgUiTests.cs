using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;
using Shiny.AiConversation.AgUi;
using static Shiny.AiConversation.Tests.FakeChatClient;

namespace Shiny.AiConversation.Tests;

public class AgUiTests
{
    static RunAgentInput UserRun(string text) => new()
    {
        ThreadId = "t1",
        RunId = "r1",
        Messages = [new AGUIUserMessage { Id = "u1", Content = new AGUIContent(text) }]
    };

    static async Task<List<BaseEvent>> Run(IAiSession session, RunAgentInput input)
    {
        var events = new List<BaseEvent>();
        await foreach (var e in session.RunAgUiAsync(input).WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token))
            events.Add(e);
        return events;
    }

    [Test]
    public async Task Text_StreamsAsOneMessageBetweenRunStartAndFinish()
    {
        var (manager, _, chat) = TestHelpers.Create();
        chat.Reply(Text("Hello "), Text("there."));
        await using var session = await manager.Start();

        var events = await Run(session, UserRun("Hi"));

        await Assert.That(events[0]).IsTypeOf<RunStartedEvent>();
        await Assert.That(events.OfType<TextMessageStartEvent>().Count()).IsEqualTo(1);
        await Assert.That(String.Concat(events.OfType<TextMessageContentEvent>().Select(x => x.Delta))).IsEqualTo("Hello there.");
        await Assert.That(events.OfType<TextMessageEndEvent>().Count()).IsEqualTo(1);
        var finished = (RunFinishedEvent)events[^1];
        await Assert.That(finished.Outcome is null or RunFinishedSuccessOutcome).IsTrue();
        await Assert.That(chat.Requests[0].Messages.Texts(ChatRole.User).Last()).IsEqualTo("Hi");
    }

    [Test]
    public async Task ToolCalls_MapToToolEventsWithResults()
    {
        var weather = AIFunctionFactory.Create(() => "sunny", "weather");
        var (manager, _, chat) = TestHelpers.Create(null, new RecordingInterceptor { OnLoaded = ctx => ctx.AddTool(weather) });
        chat.Reply(Text("Checking."), Call("c1", "weather"));
        chat.Reply(Text("It's sunny."));
        await using var session = await manager.Start();

        var events = await Run(session, UserRun("weather?"));

        await Assert.That(events.OfType<ToolCallStartEvent>().Single().ToolCallName).IsEqualTo("weather");
        await Assert.That(events.OfType<ToolCallResultEvent>().Single().Content.ToString()).IsEqualTo("sunny");
        // the text before the call is closed before the call starts, and the reply after it is a new message
        await Assert.That(events.OfType<TextMessageStartEvent>().Count()).IsEqualTo(2);
        var startIndex = events.FindIndex(x => x is ToolCallStartEvent);
        await Assert.That(events.FindIndex(x => x is TextMessageEndEvent)).IsLessThan(startIndex);
    }

    [Test]
    public async Task AskUser_EndsWithAnInterrupt_AndResumeAnswersIt()
    {
        var (manager, _, chat) = TestHelpers.Create();
        chat.Reply(Text("Which size?"), AskUser("c1", """[{ "id": "size", "text": "Small, medium or large?", "choices": [{ "id": "s", "label": "Small" }, { "id": "l", "label": "Large" }] }]"""));
        chat.Reply(Text("Large it is."));
        await using var session = await manager.Start();

        var events = await Run(session, UserRun("a coffee please"));

        var outcome = ((RunFinishedEvent)events[^1]).Outcome as RunFinishedInterruptOutcome;
        await Assert.That(outcome).IsNotNull();
        var interrupt = outcome!.Interrupts!.Single();
        await Assert.That(interrupt.Id).IsEqualTo("size");
        await Assert.That(interrupt.Reason).IsEqualTo(AiSessionAgUiExtensions.QuestionReason);
        await Assert.That(interrupt.Metadata!.Value.GetProperty("choices")[1].GetProperty("label").GetString()).IsEqualTo("Large");

        var resumed = await Run(session, new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r2",
            Resume = [new AGUIResume { InterruptId = "size", Status = ResumeStatus.Resolved, Payload = JsonElement.Parse("""{"choiceIds":["l"]}""") }]
        });

        await Assert.That(String.Concat(resumed.OfType<TextMessageContentEvent>().Select(x => x.Delta))).IsEqualTo("Large it is.");
        await Assert.That(chat.Requests[1].Messages.Texts(ChatRole.User).Last()).Contains("Large");
    }

    [Test]
    public async Task RenderUi_BecomesAnA2UISurfaceActivity()
    {
        var (manager, _, chat) = TestHelpers.Create(null, new RecordingInterceptor { OnLoaded = ctx => ctx.AddTool(new AgUiRenderTool()) });
        chat.Reply(Call("c1", AgUiRenderTool.ToolName, new Dictionary<string, object?>
        {
            ["operations"] = JsonDocument.Parse("""[{"version":"1.0","createSurface":{"surfaceId":"s1","catalogId":"https://shinylib.net/a2ui/shiny.controls/v1.json","components":[{"id":"root","component":"Text","text":"Hi"}]}}]""").RootElement.Clone()
        }));
        chat.Reply(Text("There you go."));
        await using var session = await manager.Start();

        var events = await Run(session, UserRun("show me something"));

        var activity = events.OfType<ActivitySnapshotEvent>().Single();
        await Assert.That(activity.ActivityType).IsEqualTo("a2ui-surface");
        await Assert.That(activity.Content.GetProperty("a2ui_operations")[0].GetProperty("createSurface").GetProperty("surfaceId").GetString()).IsEqualTo("s1");
        await Assert.That(events.FindIndex(x => x is ActivitySnapshotEvent)).IsGreaterThan(events.FindIndex(x => x is ToolCallEndEvent));
    }

    [Test]
    public async Task A2UIAction_GoesToTheModelAsTheNextTurn()
    {
        var (manager, _, chat) = TestHelpers.Create();
        chat.Reply(Text("Subscribed."));
        await using var session = await manager.Start();

        var events = await Run(session, new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r1",
            ForwardedProperties = JsonElement.Parse("""{"a2uiAction":{"userAction":{"name":"subscribe","surfaceId":"news","context":{"email":"a@b.c"}}}}""")
        });

        await Assert.That(events.OfType<TextMessageContentEvent>().Any()).IsTrue();
        var sent = chat.Requests[0].Messages.Texts(ChatRole.User).Last();
        await Assert.That(sent).Contains("subscribe");
        await Assert.That(sent).Contains("a@b.c");
    }

    [Test]
    public async Task Failure_IsARunError()
    {
        var (manager, _, chat) = TestHelpers.Create();
        chat.Throw(new InvalidOperationException("model down"));
        await using var session = await manager.Start();

        var events = await Run(session, UserRun("hi"));

        await Assert.That(((RunErrorEvent)events[^1]).Message).IsEqualTo("model down");
    }

    [Test]
    public async Task NothingNew_FinishesWithoutATurn()
    {
        var (manager, _, chat) = TestHelpers.Create();
        await using var session = await manager.Start();

        var events = await Run(session, new RunAgentInput { ThreadId = "t1", RunId = "r1", Messages = [new AGUIToolMessage { Id = "x", ToolCallId = "c1", Content = new AGUIContent("{}") }] });

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(chat.Requests.Count).IsEqualTo(0);
    }
}
