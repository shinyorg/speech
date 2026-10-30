using Microsoft.Extensions.AI;
using Shiny.AiConversation.Infrastructure;
using static Shiny.AiConversation.Tests.FakeChatClient;

namespace Shiny.AiConversation.Tests;

public class CompactionTests
{
    static AiSessionSettings Keep(int keep, int? maxMessages = null, bool flush = false) => new()
    {
        Compaction = new CompactionSettings { KeepRecentMessages = keep, MaxMessages = maxMessages, FlushToMemory = flush }
    };


    static async Task Exchange(IAiSession session, FakeChatClient chat, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            chat.Reply(Text($"answer {i}"));
            await session.Send(AiInput.Text($"question {i}")).WaitAsync();
        }
    }


    [Test]
    public async Task Compact_FoldsOldestMessages_IntoOneSummary()
    {
        var h = TestHelpers.CreateHarness();
        await using var session = await h.Manager.Start(Keep(2));
        await using var events = await EventCollector.Start(session);
        await Exchange(session, h.Chat, 3);

        await session.Compact();

        // 6 messages, keep the newest 2
        var call = h.Compactor.Calls.Single();
        await Assert.That(call.Messages.Count).IsEqualTo(4);
        await Assert.That(call.Previous).IsNull();

        var header = (await h.Store.GetSession(session.SessionId))!;
        await Assert.That(header.Summary!.StartsWith("summary 1")).IsTrue();

        var stored = await h.Store.GetMessages(new MessageQuery(session.SessionId));
        var summary = stored.Single(x => x.Role == MessageRole.Summary);
        await Assert.That(stored.Count(x => x.Compacted)).IsEqualTo(4);
        await Assert.That(stored.Where(x => x.Compacted).All(x => x.CompactedIntoMessageId == summary.MessageId)).IsTrue();

        await events.WaitFor(e => e.OfType<ContextCompacted>().Any());
        await Assert.That(events.Events.OfType<ContextCompacted>().Single().CompactedCount).IsEqualTo(4);
        await Assert.That(events.Events.OfType<StateChanged>().Any(x => x.State == AiSessionState.Compacting)).IsTrue();
        await Assert.That(session.State).IsEqualTo(AiSessionState.Idle);

        // the next prompt: summary at the top, then only the kept messages and the new input
        h.Chat.Reply(Text("ok"));
        await session.Send(AiInput.Text("next")).WaitAsync();
        var prompt = h.Chat.Requests[^1].Messages;
        await Assert.That(prompt.Any(m => m.Role == ChatRole.System && m.Text.StartsWith(MessageMapper.SummaryPrefix))).IsTrue();
        await Assert.That(String.Join("|", prompt.Texts(ChatRole.User))).IsEqualTo("question 3|next");
    }


    [Test]
    public async Task Compact_Again_RollsThePreviousSummaryIn()
    {
        var h = TestHelpers.CreateHarness();
        await using var session = await h.Manager.Start(Keep(1));
        await Exchange(session, h.Chat, 2);
        await session.Compact();
        await Exchange(session, h.Chat, 1);
        await session.Compact();

        await Assert.That(h.Compactor.Calls[1].Previous!.StartsWith("summary 1")).IsTrue();

        // summaries never pile up: only the newest summary message is live
        var live = await h.Store.GetMessages(new MessageQuery(session.SessionId, IncludeCompacted: false));
        await Assert.That(live.Count(x => x.Role == MessageRole.Summary)).IsEqualTo(1);
    }


    [Test]
    public async Task Compact_NeverIncludesTheReplyThatAskedPendingQuestions()
    {
        var h = TestHelpers.CreateHarness();
        await using var session = await h.Manager.Start(Keep(0));
        h.Chat.Reply(Text("Which size?"), AskUser("c1", """[{ "id": "size", "text": "Which size?" }]"""));
        await session.Send(AiInput.Text("coffee")).WaitAsync();

        await session.Compact();

        var compacted = h.Compactor.Calls.Single().Messages;
        await Assert.That(compacted.Single().Role).IsEqualTo(MessageRole.User);
        await Assert.That(session.State).IsEqualTo(AiSessionState.AwaitingUser);
    }


    [Test]
    public async Task AutoCompaction_RunsAfterATurn_OverTheThreshold()
    {
        var h = TestHelpers.CreateHarness(contextWindow: 1000);
        await using var session = await h.Manager.Start(Keep(2));
        await using var events = await EventCollector.Start(session);
        await Exchange(session, h.Chat, 2);

        h.Chat.Reply(Text("big answer"), Usage(900, 60));    // 96% >= 95%
        var result = await session.Send(AiInput.Text("big question")).WaitAsync();

        await Assert.That(result.Outcome).IsEqualTo(AiTurnOutcome.Completed);
        await events.WaitFor(e => e.OfType<ContextCompacted>().Any());
        await Assert.That(h.Compactor.Calls.Count).IsEqualTo(1);
        await Assert.That(session.Context.IsEstimate).IsTrue();
        await Assert.That(session.Context.CurrentTokens < 950).IsTrue();
    }


    [Test]
    public async Task AutoCompaction_CanBeDisabled()
    {
        var h = TestHelpers.CreateHarness(contextWindow: 1000);
        await using var session = await h.Manager.Start(new AiSessionSettings { Compaction = new CompactionSettings { AutoCompactThreshold = null } });
        h.Chat.Reply(Text("a"), Usage(990, 5));
        h.Chat.Reply(Text("b"), Usage(995, 5));
        await session.Send(AiInput.Text("1")).WaitAsync();
        await session.Send(AiInput.Text("2")).WaitAsync();

        await Assert.That(h.Compactor.Calls.Count).IsEqualTo(0);
        await Assert.That(session.Context.Pressure).IsEqualTo(ContextPressure.Critical);
    }


    [Test]
    public async Task MaxMessages_CapsThePrompt_WhenTheWindowIsUnknown()
    {
        var h = TestHelpers.CreateHarness();
        await using var session = await h.Manager.Start(Keep(10, maxMessages: 4));
        await Exchange(session, h.Chat, 3);   // 6 messages > 4

        await session.Send(AiInput.Text("wait")).Completion.ContinueWith(_ => { });
        await Assert.That(h.Compactor.Calls.Count >= 1).IsTrue();

        var live = await h.Store.GetMessages(new MessageQuery(session.SessionId, IncludeCompacted: false));
        await Assert.That(live.Count(x => x.Role != MessageRole.Summary) <= 6).IsTrue();
    }


    [Test]
    public async Task FailedSummary_DegradesToHardTrim()   // L8
    {
        var h = TestHelpers.CreateHarness();
        h.Compactor.Throw = new HttpRequestException("summarizer down");
        await using var session = await h.Manager.Start(Keep(2));
        await using var events = await EventCollector.Start(session);
        await Exchange(session, h.Chat, 2);

        await session.Compact();

        await events.WaitFor(e => e.OfType<ContextCompacted>().Any());
        var compacted = events.Events.OfType<ContextCompacted>().Single();
        await Assert.That(compacted.WasHardTrim).IsTrue();
        await Assert.That(compacted.Summary).IsNull();

        var live = await h.Store.GetMessages(new MessageQuery(session.SessionId, IncludeCompacted: false));
        await Assert.That(live.Count).IsEqualTo(2);
    }


    [Test]
    public async Task OnCompacted_InterceptorsRun()
    {
        var compacted = new List<CompactionContext>();
        var interceptor = new CompactionSpy(compacted);
        var h = TestHelpers.CreateHarness(interceptors: interceptor);
        await using var session = await h.Manager.Start(Keep(0));
        await Exchange(session, h.Chat, 1);

        await session.Compact();

        await Assert.That(compacted.Single().Summary!.Role).IsEqualTo(MessageRole.Summary);
    }


    [Test]
    public async Task FlushToMemory_LetsTheModelSaveNotesBeforeSummarizing()
    {
        var h = TestHelpers.CreateHarness(memory: new AiMemoryOptions());
        await using var session = await h.Manager.Start(Keep(0, flush: true));
        await Exchange(session, h.Chat, 1);

        h.Chat.Reply(Call("m1", SessionMemory.ToolName, new()
        {
            ["command"] = "create", ["path"] = "/facts.md", ["content"] = "User's name is Allan", ["scope"] = "global"
        }));
        h.Chat.Reply(Text("OK"));
        await session.Compact();

        var flushRequest = h.Chat.Requests[1];
        await Assert.That(flushRequest.Options!.Tools!.Single().Name).IsEqualTo(SessionMemory.ToolName);
        await Assert.That(flushRequest.Messages[^1].Text.Contains("about to be summarized")).IsTrue();

        var note = await h.MemoryStore.Get(MemoryScope.Global, null, "/facts.md");
        await Assert.That(note!.Content).IsEqualTo("User's name is Allan");
        await Assert.That(h.Compactor.Calls.Count).IsEqualTo(1);

        // the flush exchange itself is not persisted
        var stored = await h.Store.GetMessages(new MessageQuery(session.SessionId));
        await Assert.That(stored.Any(x => x.Text == "OK")).IsFalse();
    }


    [Test]
    public async Task DefaultCompactor_AsksTheModel()
    {
        var chat = new FakeChatClient().Reply(Text("  The user wants coffee.  "));
        var compactor = new ChatClientContextCompactor(new FakeChatClientProvider(chat));

        var summary = await compactor.Summarize("Earlier: tea.", [
            new AiMessageDocument("1", "s", 1, 1, DateTimeOffset.UtcNow, MessageRole.User, MessageSource.Typed, MessageStatus.Complete, [new TextPart("coffee please")], "coffee please", null, null, false, null, "r")
        ], CancellationToken.None);

        await Assert.That(summary).IsEqualTo("The user wants coffee.");
        var input = chat.Requests.Single().Messages[1].Text;
        await Assert.That(input.Contains("<previous_summary>") && input.Contains("User: coffee please")).IsTrue();
    }


    sealed class CompactionSpy(List<CompactionContext> list) : ISessionInterceptor
    {
        public Task OnCompacted(CompactionContext context)
        {
            list.Add(context);
            return Task.CompletedTask;
        }
    }
}
