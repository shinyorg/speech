namespace Shiny.AiConversation;

/// <summary>
/// What the user sends to a session.
/// </summary>
public record AiInput(IReadOnlyList<MessagePart> Parts, MessageSource Source)
{
    /// <summary>
    /// Typed (or otherwise plain) text.
    /// </summary>
    public static AiInput Text(string text, MessageSource source = MessageSource.Typed)
        => new([new TextPart(text, Audience.Displayed)], source);

    /// <summary>
    /// What speech-to-text heard. The AI is told its reply will be spoken.
    /// </summary>
    public static AiInput Voice(string text)
        => new([new TextPart(text, Audience.Displayed)], MessageSource.Voice);

    /// <summary>
    /// An answer to one of the session's <see cref="IAiSession.PendingQuestions"/> by choice id.
    /// </summary>
    /// <param name="question">The question being answered.</param>
    /// <param name="choiceIds">The selected choice ids.</param>
    public static AiInput Answer(AiQuestion question, params string[] choiceIds)
    {
        var labels = choiceIds
            .Select(id => question.Choices?.FirstOrDefault(c => c.Id == id)?.Label ?? id)
            .ToArray();

        return new([new AnswerPart(question.Id, choiceIds, String.Join(", ", labels))], MessageSource.ChoiceAnswer);
    }
}
