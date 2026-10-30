using System.Text.Json.Serialization;

namespace Shiny.AiConversation;

/// <summary>
/// Source-generated (AOT/trim safe) JSON metadata for every persisted type. Store implementations
/// should serialize documents with this context.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true
)]
[JsonSerializable(typeof(AiSessionDocument))]
[JsonSerializable(typeof(AiMessageDocument))]
[JsonSerializable(typeof(List<AiQuestion>))]
public partial class AiConversationJsonContext : JsonSerializerContext;
