namespace Shiny.AiConversation;

/// <summary>
/// What the user sends to a session.
/// </summary>
public record AiInput(IReadOnlyList<MessagePart> Parts, MessageSource Source)
{
    /// <summary>
    /// Extra system instructions for this turn only (not stored), e.g. "be concise" from the voice driver.
    /// </summary>
    public IReadOnlyList<string> Instructions { get; init; } = [];

    /// <summary>
    /// Returns a copy with <paramref name="instructions"/> added to <see cref="Instructions"/>.
    /// </summary>
    public AiInput WithInstructions(params string[] instructions)
        => this with { Instructions = [.. this.Instructions, .. instructions] };

    /// <summary>
    /// Images to send with this input. The session stores them (per <see cref="AiImage.Retention"/>) and the
    /// stored message references them with <see cref="ImagePart"/>s.
    /// </summary>
    public IReadOnlyList<AiImage> Images { get; init; } = [];

    /// <summary>
    /// Returns a copy with an image attached.
    /// </summary>
    public AiInput WithImage(ReadOnlyMemory<byte> data, string mediaType, string? description = null, ImageRetention retention = ImageRetention.DropAfterTurn)
        => this with { Images = [.. this.Images, new AiImage(data, mediaType, description, retention)] };

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
