using System.Text.Json;
using Microsoft.Extensions.AI;
using Shiny.AiConversation.Infrastructure;

namespace Shiny.AiConversation.Tests;

public class MessageMapperTests
{
    static AiMessageDocument Doc(
        MessageRole role,
        IReadOnlyList<MessagePart> parts,
        MessageStatus status = MessageStatus.Complete,
        string? spokenText = null,
        bool compacted = false
    ) => new(
        Guid.NewGuid().ToString(),
        "s1",
        1,
        AiMessageDocument.CurrentSchemaVersion,
        DateTimeOffset.UtcNow,
        role,
        role == MessageRole.User ? MessageSource.Typed : MessageSource.Model,
        status,
        parts,
        MessageMapper.GetText(parts),
        spokenText,
        null,
        compacted,
        null,
        "run"
    );


    [Test]
    public async Task ToolLoop_RoundTrips()
    {
        List<ChatMessage> response =
        [
            new(ChatRole.Assistant, [new TextContent("Let me check. "), new FunctionCallContent("c1", "weather", new Dictionary<string, object?> { ["city"] = "Toronto" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "sunny")]),
            new(ChatRole.Assistant, [new TextContent("It's sunny.")])
        ];

        var parts = MessageMapper.ToParts(response);
        await Assert.That(parts.Count).IsEqualTo(4);
        await Assert.That(((ToolResultPart)parts[2]).Name).IsEqualTo("weather");

        var back = MessageMapper.ToChatMessages([Doc(MessageRole.Assistant, parts)]);
        await Assert.That(back.Count).IsEqualTo(3);
        await Assert.That(back[0].Role).IsEqualTo(ChatRole.Assistant);
        await Assert.That(back[0].Text).IsEqualTo("Let me check. ");

        var call = back[0].Contents.OfType<FunctionCallContent>().Single();
        await Assert.That(call.Name).IsEqualTo("weather");
        await Assert.That(call.Arguments!["city"]!.ToString()).IsEqualTo("Toronto");

        await Assert.That(back[1].Role).IsEqualTo(ChatRole.Tool);
        await Assert.That(back[1].Contents.OfType<FunctionResultContent>().Single().Result).IsEqualTo("sunny");
        await Assert.That(back[2].Text).IsEqualTo("It's sunny.");
    }


    [Test]
    public async Task Interrupted_SendsWhatWasHeard()   // P4
    {
        var doc = Doc(MessageRole.Assistant, [new TextPart("The full reply that was never fully spoken.")], MessageStatus.Interrupted, "The full reply");
        var messages = MessageMapper.ToChatMessages([doc]);

        await Assert.That(messages.Single().Text).IsEqualTo($"The full reply {MessageMapper.InterruptedMarker}");
    }


    [Test]
    public async Task UnansweredToolCall_IsDropped()
    {
        var doc = Doc(MessageRole.Assistant, [new TextPart("Checking"), new ToolCallPart("c1", "weather", "{}")], MessageStatus.Interrupted);
        var messages = MessageMapper.ToChatMessages([doc]);

        await Assert.That(messages.Single().Contents.OfType<FunctionCallContent>().Any()).IsFalse();
    }


    [Test]
    public async Task Skips_Failed_Streaming_AndCompacted()
    {
        var messages = MessageMapper.ToChatMessages(
        [
            Doc(MessageRole.User, [new TextPart("hi")]),
            Doc(MessageRole.Assistant, [new TextPart("partial")], MessageStatus.Failed),
            Doc(MessageRole.Assistant, [new TextPart("in progress")], MessageStatus.Streaming),
            Doc(MessageRole.User, [new TextPart("old")], compacted: true)
        ]);

        await Assert.That(messages.Count).IsEqualTo(1);
        await Assert.That(messages[0].Text).IsEqualTo("hi");
    }


    [Test]
    public async Task Answer_IsSentAsItsLabel()
    {
        var question = new AiQuestion("size", "What size?", [new AiChoice("s", "Small"), new AiChoice("l", "Large")]);
        var input = AiInput.Answer(question, "l");
        var messages = MessageMapper.ToChatMessages([Doc(MessageRole.User, input.Parts)]);

        await Assert.That(messages.Single().Text).IsEqualTo("Large");
    }


    [Test]
    public async Task Documents_RoundTripThroughSourceGeneratedJson()
    {
        var doc = Doc(MessageRole.Assistant,
        [
            new TextPart("Hello"),
            new ToolCallPart("c1", "ask_user", "{\"questions\":[]}"),
            new ToolResultPart("c1", "ask_user", "shown"),
            new AnswerPart("q1", ["a"], "A")
        ]) with { Usage = new UsageSnapshot(1, 2, null, null, 3) };

        var json = JsonSerializer.Serialize(doc, AiConversationJsonContext.Default.AiMessageDocument);
        var back = JsonSerializer.Deserialize(json, AiConversationJsonContext.Default.AiMessageDocument)!;

        await Assert.That(json.Contains("\"kind\":\"toolCall\"")).IsTrue();
        await Assert.That(back.Parts.Count).IsEqualTo(4);
        await Assert.That(back.Parts[1]).IsEqualTo(doc.Parts[1]);
        await Assert.That(((AnswerPart)back.Parts[3]).ChoiceIds.Single()).IsEqualTo("a");
        await Assert.That(back.Usage).IsEqualTo(doc.Usage);

        var header = new AiSessionDocument("s1", 1, 3, "Title", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new AiSessionSettings { Profile = "cooking", Tags = ["a"] }, null,
            [new AiQuestion("q", "Which?", [new AiChoice("x", "X")])],
            new SessionUsage(1, 2, 0, 0, 3, 1), ContextSnapshot.Empty, 2, 2);

        var headerJson = JsonSerializer.Serialize(header, AiConversationJsonContext.Default.AiSessionDocument);
        var headerBack = JsonSerializer.Deserialize(headerJson, AiConversationJsonContext.Default.AiSessionDocument)!;
        await Assert.That(headerBack.Settings.Profile).IsEqualTo("cooking");
        await Assert.That(headerBack.PendingQuestions.Single().Choices!.Single().Label).IsEqualTo("X");
        await Assert.That(headerBack.Usage).IsEqualTo(header.Usage);
    }


    [Test]
    public async Task IdGenerator_ProducesSortableUlids()
    {
        var a = IdGenerator.New();
        await Task.Delay(2);
        var b = IdGenerator.New();

        await Assert.That(a.Length).IsEqualTo(26);
        await Assert.That(String.CompareOrdinal(a, b) < 0).IsTrue();
    }
}
