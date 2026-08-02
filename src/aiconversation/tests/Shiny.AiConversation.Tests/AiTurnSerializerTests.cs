namespace Shiny.AiConversation.Tests;

/// <summary>
/// The parser is deliberately forgiving - models pad structured replies with prose and markdown fences
/// often enough that a strict parse would throw away otherwise good turns.
/// </summary>
public class AiTurnSerializerTests
{
    [Test]
    public async Task Parse_PlainJson()
    {
        var turn = AiTurnSerializer.Parse("""{"reply":"Hi","questions":[]}""");

        await Assert.That(turn).IsNotNull();
        await Assert.That(turn!.Reply).IsEqualTo("Hi");
        await Assert.That(turn.ExpectsResponse).IsFalse();
    }

    [Test]
    public async Task Parse_IgnoresBracesInsideReplyText()
    {
        // Naive brace counting would end the object at the '}' inside the reply string.
        var turn = AiTurnSerializer.Parse("""{"reply":"Use {0} as the placeholder","questions":[]}""");

        await Assert.That(turn).IsNotNull();
        await Assert.That(turn!.Reply).IsEqualTo("Use {0} as the placeholder");
    }

    [Test]
    public async Task Parse_HandlesEscapedQuotesInReply()
    {
        var turn = AiTurnSerializer.Parse("""{"reply":"He said \"yes\" to it","questions":[]}""");

        await Assert.That(turn).IsNotNull();
        await Assert.That(turn!.Reply).IsEqualTo("He said \"yes\" to it");
    }

    [Test]
    public async Task Parse_ReturnsNullForNonJson()
        => await Assert.That(AiTurnSerializer.Parse("Just a normal sentence.")).IsNull();

    [Test]
    public async Task Parse_ReturnsNullWhenReplyMissing()
        => await Assert.That(AiTurnSerializer.Parse("""{"questions":[]}""")).IsNull();

    [Test]
    public async Task Parse_ReturnsNullForTruncatedJson()
        => await Assert.That(AiTurnSerializer.Parse("""{"reply":"Hi","questions":[""")).IsNull();

    [Test]
    public async Task Serializer_RoundTripsQuestions()
    {
        AiQuestion[] questions =
        [
            new("time", "Which time?", [new AiChoice("am", "9:00 am"), new AiChoice("pm", "2:00 pm")]),
            new("size", "How many?", null, AllowMultiple: true)
        ];

        var round = AiTurnSerializer.DeserializeQuestions(AiTurnSerializer.SerializeQuestions(questions));

        await Assert.That(round).IsNotNull();
        await Assert.That(round!.Length).IsEqualTo(2);
        await Assert.That(round[0].Choices![1].Label).IsEqualTo("2:00 pm");
        await Assert.That(round[1].AllowMultiple).IsTrue();
        await Assert.That(round[1].HasChoices).IsFalse();
    }

    [Test]
    public async Task Serializer_ReturnsNullForGarbage()
        => await Assert.That(AiTurnSerializer.DeserializeQuestions("not json")).IsNull();
}
