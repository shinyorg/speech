using Microsoft.Extensions.AI;
using Shiny.AiConversation.Voice;
using Shiny.AiConversation.Voice.Infrastructure;
using static Shiny.AiConversation.Tests.FakeChatClient;

namespace Shiny.AiConversation.Tests;

public class AnswerMatcherTests
{
    static readonly AiQuestion Size = new("size", "Small, medium or large?", [new("s", "Small"), new("m", "Medium"), new("l", "Large")]);
    static readonly AiQuestion Cream = new("cream", "Do you want cream?", [new("yes", "Yes"), new("no", "No")]);
    static readonly AiQuestion Toppings = new("top", "Which toppings?", [new("c", "Cheese"), new("p", "Pepperoni"), new("o", "Olives")], AllowMultiple: true);

    static string? Ids(string text, params AiQuestion[] questions)
        => AnswerMatcher.Match(text, questions) is { } m ? $"{m.Question.Id}:{String.Join("+", m.ChoiceIds)}" : null;

    [Test]
    [Arguments("large", "size:l")]
    [Arguments("Large.", "size:l")]
    [Arguments("the large", "size:l")]
    [Arguments("I'll take medium please", "size:m")]
    [Arguments("the second one", "size:m")]
    [Arguments("option three", "size:l")]
    [Arguments("option one", "size:s")]
    [Arguments("number 1", "size:s")]
    [Arguments("the last one", "size:l")]
    public async Task Choices(string text, string expected)
        => await Assert.That(Ids(text, Size)).IsEqualTo(expected);

    [Test]
    [Arguments("yeah", "cream:yes")]
    [Arguments("Yes please!", "cream:yes")]
    [Arguments("nope", "cream:no")]
    [Arguments("no thanks", "cream:no")]
    public async Task YesNo(string text, string expected)
        => await Assert.That(Ids(text, Cream)).IsEqualTo(expected);

    [Test]
    public async Task Multiple_WhenAllowed()
    {
        await Assert.That(Ids("cheese and olives", Toppings)).IsEqualTo("top:c+o");
        await Assert.That(Ids("small and large", Size)).IsNull();   // single-choice question
    }

    [Test]
    [Arguments("Small, medium or large?")]          // the AI's own voice reading its options
    [Arguments("I think large would be nice for the party")]
    [Arguments("cancel this order")]
    [Arguments("yes")]                               // Size has no yes/no choices
    public async Task NotAnAnswer(string text)
        => await Assert.That(Ids(text, Size)).IsNull();

    [Test]
    public async Task QuestionsWithoutChoices_NeverMatch()
        => await Assert.That(Ids("Tuesday", new AiQuestion("day", "Which day?"))).IsNull();
}


public class AnswerInterruptionTests
{
    static VoiceOptions Opts(bool answers = true) => new()
    {
        Interruption = InterruptionMode.QuietWords,
        AnswerInterruptions = answers,
        FollowUpTimeout = TimeSpan.FromSeconds(5)
    };

    const string SizeQuestion = """[{ "id": "size", "text": "Small, medium or large?", "choices": [{ "id": "s", "label": "Small" }, { "id": "m", "label": "Medium" }, { "id": "l", "label": "Large" }] }]""";


    [Test]
    public async Task AnsweringWhileItSpeaks_StopsTheReply_AndSendsTheChoice()
    {
        await using var h = new VoiceHarness();
        h.Chat.Reply(Text("Sure. What size would you like? Small, medium or large?"), AskUser("c1", SizeQuestion));
        h.Chat.Reply(Text("One large coffee coming up."));
        h.Tts.Hold();
        await h.Start(Opts());

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("a coffee");
        await h.WaitForState(VoiceState.Speaking);

        // the recognizer hearing the AI read its own options is not an answer
        h.Stt.Final("Small, medium or large?");
        await Task.Delay(50);
        await Assert.That(h.Voice.State).IsEqualTo(VoiceState.Speaking);

        h.Stt.Final("large");
        await VoiceHarness.WaitUntil(() => h.Chat.Requests.Count == 2);
        h.Tts.Release();

        var interrupted = h.VoiceEvents.OfType<SpeechInterrupted>().Single();
        await Assert.That(interrupted.Reason).IsEqualTo(InterruptionReason.Answer);
        await Assert.That(interrupted.HeardText).IsEqualTo("large");

        // structured answer, sent as voice (so the reply stays speakable), after the interrupted question
        var request = h.Chat.Requests[1].Messages;
        await Assert.That(request.Texts(ChatRole.User).Last()).IsEqualTo("Large");
        await Assert.That(request[0].Text.Contains("spoken aloud")).IsTrue();

        var stored = await h.Messages();
        var answer = stored.Last(x => x.Role == MessageRole.User);
        await Assert.That(answer.Source).IsEqualTo(MessageSource.Voice);
        await Assert.That(answer.Parts.OfType<AnswerPart>().Single().ChoiceIds.Single()).IsEqualTo("l");
        await Assert.That(stored.First(x => x.Role == MessageRole.Assistant).Status).IsEqualTo(MessageStatus.Interrupted);
    }


    [Test]
    public async Task FollowUpAnswer_BecomesAStructuredChoice()
    {
        await using var h = new VoiceHarness();
        h.Chat.Reply(Text("Which size?"), AskUser("c1", SizeQuestion));
        h.Chat.Reply(Text("Medium it is."));
        await h.Start(Opts());

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("a coffee");
        await h.WaitForState(VoiceState.FollowUp);
        h.Stt.Final("the second one");

        await VoiceHarness.WaitUntil(() => h.Chat.Requests.Count == 2);
        await VoiceHarness.WaitUntil(() => h.Tts.Spoken.Count == 2);
        var answer = (await h.Messages()).Last(x => x.Role == MessageRole.User);
        await Assert.That(answer.Parts.OfType<AnswerPart>().Single().ChoiceIds.Single()).IsEqualTo("m");
        await Assert.That(answer.Text).IsEqualTo("Medium");
    }


    [Test]
    public async Task CanBeTurnedOff()
    {
        await using var h = new VoiceHarness();
        h.Chat.Reply(Text("What size? Small, medium or large?"), AskUser("c1", SizeQuestion));
        h.Tts.Hold();
        await h.Start(Opts(answers: false));

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("a coffee");
        await h.WaitForState(VoiceState.Speaking);

        h.Stt.Final("large");
        await Task.Delay(80);
        await Assert.That(h.Voice.State).IsEqualTo(VoiceState.Speaking);
        await Assert.That(h.VoiceEvents.OfType<SpeechInterrupted>().Any()).IsFalse();
        h.Tts.Release();
    }
}
