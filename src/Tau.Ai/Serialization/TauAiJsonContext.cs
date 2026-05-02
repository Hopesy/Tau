using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tau.Ai.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(UserMessage))]
[JsonSerializable(typeof(AssistantMessage))]
[JsonSerializable(typeof(ToolResultMessage))]
[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(TextContent))]
[JsonSerializable(typeof(ThinkingContent))]
[JsonSerializable(typeof(ImageContent))]
[JsonSerializable(typeof(ToolCallContent))]
[JsonSerializable(typeof(Tool))]
[JsonSerializable(typeof(Model))]
[JsonSerializable(typeof(ModelCompatibility))]
[JsonSerializable(typeof(VercelGatewayRouting))]
[JsonSerializable(typeof(Usage))]
[JsonSerializable(typeof(ModelCost))]
[JsonSerializable(typeof(StreamOptions))]
[JsonSerializable(typeof(SimpleStreamOptions))]
[JsonSerializable(typeof(IReadOnlyList<ChatMessage>))]
[JsonSerializable(typeof(IReadOnlyList<ContentBlock>))]
[JsonSerializable(typeof(IReadOnlyList<Tool>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
internal partial class TauAiJsonContext : JsonSerializerContext;
