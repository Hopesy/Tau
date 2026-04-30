namespace Tau.Ai;

public record StreamOptions
{
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public float? TopP { get; init; }
    public string? ApiKey { get; init; }
    public StreamTransport Transport { get; init; } = StreamTransport.Sse;
    public CacheRetention CacheRetention { get; init; } = CacheRetention.None;
    public string? SessionId { get; init; }
    public IDictionary<string, string>? Headers { get; init; }
    public TimeSpan? MaxRetryDelay { get; init; }
    public IDictionary<string, object>? Metadata { get; init; }
}

public record SimpleStreamOptions : StreamOptions
{
    public ThinkingLevel? Reasoning { get; init; }
}

public enum CacheRetention
{
    None,
    Short,
    Long
}

public enum StreamTransport
{
    Sse,
    WebSocket,
    Auto
}

public enum ThinkingLevel
{
    Minimal,
    Low,
    Medium,
    High,
    ExtraHigh
}
