namespace Tau.Ai;

public record Model
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Api { get; init; }
    public required string Provider { get; init; }
    public string? BaseUrl { get; init; }
    public bool Reasoning { get; init; }
    /// <summary>将通用 thinking level 映射为 provider/model 专用值；null 表示该级别不支持。</summary>
    public IReadOnlyDictionary<string, string?>? ThinkingLevelMap { get; init; }
    public IReadOnlyList<string> InputModalities { get; init; } = ["text"];
    public ModelCost? Cost { get; init; }
    public int? ContextWindow { get; init; }
    public int? MaxOutputTokens { get; init; }
    public IDictionary<string, string>? Headers { get; init; }
    /// <summary>发送给 OpenAI-compatible provider 的额外采样参数。</summary>
    public IDictionary<string, object>? SamplingParams { get; init; }
    public ModelCompatibility? Compat { get; init; }
}

public record ModelCompatibility
{
    /// <summary>Anthropic 服务端拒答时允许自动尝试的备用模型。</summary>
    public IReadOnlyList<ModelFallback>? AllowedFallbackModels { get; init; }
    public bool? SupportsStore { get; init; }
    public bool? SupportsDeveloperRole { get; init; }
    public bool? SupportsReasoningEffort { get; init; }
    public IReadOnlyDictionary<string, string>? ReasoningEffortMap { get; init; }
    public bool? SupportsUsageInStreaming { get; init; }
    /// <summary>provider 可能省略 finish_reason 时是否允许按流结束推断停止原因。</summary>
    public bool? SupportsFinishReason { get; init; }
    public string? MaxTokensField { get; init; }
    public bool? RequiresToolResultName { get; init; }
    public bool? RequiresAssistantAfterToolResult { get; init; }
    public bool? RequiresThinkingAsText { get; init; }
    public bool? RequiresReasoningContentOnAssistantMessages { get; init; }
    public string? ThinkingFormat { get; init; }
    public IDictionary<string, object>? OpenRouterRouting { get; init; }
    public VercelGatewayRouting? VercelGatewayRouting { get; init; }
    public bool? ZaiToolStream { get; init; }
    public bool? SupportsStrictMode { get; init; }
    public string? CacheControlFormat { get; init; }
    public bool? SendSessionAffinityHeaders { get; init; }
    public bool? SupportsLongCacheRetention { get; init; }
    public bool? SupportsTemperature { get; init; }
    public bool? ForceAdaptiveThinking { get; init; }
    public bool? SupportsEagerToolInputStreaming { get; init; }
    public bool? SupportsCacheControlOnTools { get; init; }
    public bool? AllowEmptySignature { get; init; }
    public bool? SupportsDisabledThinking { get; init; }
    /// <summary>是否支持 OpenAI grammar custom tool。</summary>
    public bool? SupportsOpenAiGrammarTools { get; init; }
    /// <summary>是否支持通过 additional_tools 输入项延迟加载工具。</summary>
    public bool? SupportsAdditionalTools { get; init; }
    /// <summary>是否支持客户端 tool search 延迟加载工具。</summary>
    public bool? SupportsToolSearch { get; init; }
    /// <summary>是否支持显式 prompt cache 参数。</summary>
    public bool? SupportsExplicitPromptCacheMode { get; init; }
    /// <summary>是否支持 max_output_tokens 字段。</summary>
    public bool? SupportsMaxOutputTokens { get; init; }
    /// <summary>是否支持 Anthropic tool reference 延迟工具。</summary>
    public bool? SupportsToolReferences { get; init; }
    /// <summary>是否支持 Bedrock 严格工具 schema。</summary>
    public bool? SupportsStrictTools { get; init; }
    /// <summary>是否支持模型级 deferred tool loading。</summary>
    public bool? SupportsDeferredTools { get; init; }
    /// <summary>OpenAI-compatible 请求使用的 thinking token budget 字段名。</summary>
    public string? ThinkingTokenBudgetField { get; init; }
    /// <summary>OpenAI-compatible chat template 参数。</summary>
    public IDictionary<string, object>? ChatTemplateArgs { get; init; }
}

/// <summary>Anthropic 服务端回退模型及其可选计费信息。</summary>
/// <param name="Provider">备用模型所属 provider。</param>
/// <param name="Model">备用模型标识。</param>
/// <param name="Cost">备用模型计费信息。</param>
public sealed record ModelFallback(string Provider, string Model, ModelCost? Cost = null);

public record VercelGatewayRouting
{
    public IReadOnlyList<string>? Only { get; init; }
    public IReadOnlyList<string>? Order { get; init; }
}

public record struct ModelCost(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal? CacheReadPerMillion = null,
    decimal? CacheWritePerMillion = null,
    IReadOnlyList<ModelCostTier>? Tiers = null);

/// <summary>按请求累计输入 token 阈值选择的模型计费层。</summary>
public readonly record struct ModelCostTier(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal? CacheReadPerMillion,
    decimal? CacheWritePerMillion,
    long InputTokensAbove);

public readonly record struct UsageCost(
    decimal Input,
    decimal Output,
    decimal CacheRead = 0,
    decimal CacheWrite = 0)
{
    public decimal Total => Input + Output + CacheRead + CacheWrite;
}
