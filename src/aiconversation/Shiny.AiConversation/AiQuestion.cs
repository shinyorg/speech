using System.Text.Json.Serialization;

namespace Shiny.AiConversation;

/// <summary>
/// Something the AI needs from the user, produced by the built-in <c>ask_user</c> tool.
/// </summary>
/// <param name="Id">A stable identifier, used to correlate an answer back to the question.</param>
/// <param name="Text">The question as it should be shown.</param>
/// <param name="Choices">The valid answers when there is a small fixed set of them; null or empty means free-form.</param>
/// <param name="AllowMultiple">True when more than one choice may be selected.</param>
public record AiQuestion(
    string Id,
    string Text,
    IReadOnlyList<AiChoice>? Choices = null,
    bool AllowMultiple = false
)
{
    /// <summary>
    /// True when the question offers a fixed set of answers.
    /// </summary>
    [JsonIgnore]
    public bool HasChoices => this.Choices is { Count: > 0 };
}

/// <summary>
/// One selectable answer to an <see cref="AiQuestion"/>.
/// </summary>
/// <param name="Id">A stable identifier for the choice.</param>
/// <param name="Label">Short human text; this is what is shown and sent back as the answer.</param>
public record AiChoice(string Id, string Label);
