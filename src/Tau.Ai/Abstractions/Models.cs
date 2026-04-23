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
