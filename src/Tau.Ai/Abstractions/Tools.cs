using System.Text.Json;

namespace Tau.Ai;

public record Tool(
    string Name,
    string Description,
    JsonElement ParameterSchema)
{
    /// <summary>可选的 constrained sampling 配置。</summary>
    public ConstrainedSamplingConfig? ConstrainedSampling { get; init; }
    /// <summary>grammar 变体，键通常为 openai_lark 或 openai_regex。</summary>
    public IReadOnlyDictionary<string, string>? GrammarVariants => ConstrainedSampling?.Variants;
}

/// <summary>工具约束采样配置。</summary>
public sealed record ConstrainedSamplingConfig
{
    /// <summary>约束类型，json_schema 或 grammar。</summary>
    public required string Type { get; init; }
    /// <summary>JSON Schema strict 策略。</summary>
    public string? Strict { get; init; }
    /// <summary>grammar 变体定义。</summary>
    public IReadOnlyDictionary<string, string>? Variants { get; init; }
}

public record struct LlmContext(
    string? SystemPrompt,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<Tool>? Tools);
