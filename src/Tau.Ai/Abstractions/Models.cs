namespace Tau.Ai;

public record Model
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Api { get; init; }
    public required string Provider { get; init; }
    public string? BaseUrl { get; init; }
    public bool Reasoning { get; init; }
    public IReadOnlyList<string> InputModalities { get; init; } = ["text"];
    public ModelCost? Cost { get; init; }
    public int? ContextWindow { get; init; }
    public int? MaxOutputTokens { get; init; }
    public IDictionary<string, string>? Headers { get; init; }
    public ModelCompatibility? Compat { get; init; }
}

public record ModelCompatibility
{
    public bool? SupportsStore { get; init; }
    public bool? SupportsDeveloperRole { get; init; }
    public bool? SupportsReasoningEffort { get; init; }
    public IReadOnlyDictionary<string, string>? ReasoningEffortMap { get; init; }
    public bool? SupportsUsageInStreaming { get; init; }
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
}

public record VercelGatewayRouting
{
    public IReadOnlyList<string>? Only { get; init; }
    public IReadOnlyList<string>? Order { get; init; }
}

public record struct ModelCost(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal? CacheReadPerMillion = null,
    decimal? CacheWritePerMillion = null);

public readonly record struct UsageCost(
    decimal Input,
    decimal Output,
    decimal CacheRead = 0,
    decimal CacheWrite = 0)
{
    public decimal Total => Input + Output + CacheRead + CacheWrite;
}
