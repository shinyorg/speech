using Microsoft.Extensions.AI;
using static Shiny.AiConversation.Tests.FakeChatClient;

namespace Shiny.AiConversation.Tests;

/// <summary>
/// Pins the Microsoft.Extensions.AI behaviour the session design depends on (the Phase 1 spike).
/// If a package update breaks one of these, the session's turn pipeline needs revisiting.
/// </summary>
public class MeaiBehaviourTests
{
    [Test]
    public async Task Terminate_StopsTheStreamingToolLoop()
    {
        var chat = new FakeChatClient()
            .Reply(Text("Which size?"), Call("c1", "ask"))
            .Reply(Text("should never be requested"));

        var tool = AIFunctionFactory.Create(() =>
        {
            FunctionInvokingChatClient.CurrentContext!.Terminate = true;
            return "shown";
        }, "ask");

        var client = chat.AsBuilder().UseFunctionInvocation().Build();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { Tools = [tool] }))
            updates.Add(u);

        await Assert.That(chat.Requests.Count).IsEqualTo(1);
        var messages = updates.ToChatResponse().Messages;
        await Assert.That(messages.Count).IsEqualTo(2);
        await Assert.That(messages[1].Contents.OfType<FunctionResultContent>().Single().Result?.ToString()).IsEqualTo("shown");
    }


    [Test]
    public async Task Streaming_ReportsUsagePerRequest_NotAggregated()
    {
        var chat = new FakeChatClient()
            .Reply(Call("c1", "weather"), Usage(100, 10))
            .Reply(Text("Sunny."), Usage(130, 5));

        var tool = AIFunctionFactory.Create(() => "sunny", "weather");
        var client = chat.AsBuilder().UseFunctionInvocation().Build();

        var usages = new List<UsageDetails>();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { Tools = [tool] }))
        {
            updates.Add(u);
            usages.AddRange(u.Contents.OfType<UsageContent>().Select(x => x.Details));
        }

        await Assert.That(usages.Count).IsEqualTo(2);
        await Assert.That(usages[1].InputTokenCount).IsEqualTo(130);

        // ToChatResponse sums them, which overstates the context size
        await Assert.That(updates.ToChatResponse().Usage!.InputTokenCount).IsEqualTo(230);
    }
}
