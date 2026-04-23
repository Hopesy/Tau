using System.Text.Json;

namespace Tau.Ai;

public record Tool(
    string Name,
    string Description,
    JsonElement ParameterSchema);

public record struct LlmContext(
    string? SystemPrompt,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<Tool>? Tools);
