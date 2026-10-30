using System.Text.Json.Serialization;

namespace Shiny.AiConversation;

/// <summary>
/// Who a message part is for. The voice driver speaks only <see cref="Spoken"/> parts and the chat UI
/// shows <see cref="Displayed"/> parts; <see cref="ModelOnly"/> parts (tool calls, memory writes) are
/// only ever sent back to the model.
/// </summary>
[Flags]
public enum Audience
{
    None = 0,
    Spoken = 1,
    Displayed = 2,
    ModelOnly = 4
}

/// <summary>
/// One piece of a stored message. This is the persisted schema; it is mapped to and from
/// Microsoft.Extensions.AI types in a single place so provider types never leak into storage.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ToolCallPart), "toolCall")]
[JsonDerivedType(typeof(ToolResultPart), "toolResult")]
[JsonDerivedType(typeof(AnswerPart), "answer")]
[JsonDerivedType(typeof(ImagePart), "image")]
public abstract record MessagePart(Audience Audience);

public record TextPart(string Text, Audience Audience = Audience.Spoken | Audience.Displayed) : MessagePart(Audience);

public record ToolCallPart(string CallId, string Name, string ArgumentsJson) : MessagePart(Audience.ModelOnly);

public record ToolResultPart(string CallId, string Name, string Result, bool IsError = false) : MessagePart(Audience.ModelOnly);

/// <summary>
/// An image stored as an attachment (never inline). <paramref name="Retention"/> decides how long the model sees it.
/// </summary>
public record ImagePart(AttachmentRef Attachment, string? Description = null, ImageRetention Retention = ImageRetention.DropAfterTurn, Audience Audience = Audience.Displayed) : MessagePart(Audience);

/// <summary>
/// The user's answer to an <see cref="AiQuestion"/>. <paramref name="Text"/> is what is shown and sent to the model.
/// </summary>
public record AnswerPart(string QuestionId, IReadOnlyList<string> ChoiceIds, string Text) : MessagePart(Audience.Displayed);
