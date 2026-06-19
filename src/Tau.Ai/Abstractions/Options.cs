using System.Text.Json.Serialization;

namespace Tau.Ai;

public sealed record ProviderResponse(int Status, IReadOnlyDictionary<string, string> Headers);

public sealed record ThinkingBudgets
{
    public int? Minimal { get; init; }
    public int? Low { get; init; }
    public int? Medium { get; init; }
    public int? High { get; init; }
}

public record StreamOptions
{
    private readonly StreamTransport _transport = StreamTransport.Sse;
    private readonly CacheRetention _cacheRetention = CacheRetention.None;
    private readonly bool _transportWasSet;
    private readonly bool _cacheRetentionWasSet;

    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public float? TopP { get; init; }
    public string? ApiKey { get; init; }
    [JsonIgnore]
    public CancellationToken Signal { get; init; }
    [JsonIgnore]
    public Func<ProviderResponse, Model, ValueTask>? OnResponse { get; init; }
    [JsonIgnore]
    public Func<object, Model, ValueTask<object?>>? OnPayload { get; init; }
    public StreamTransport Transport
    {
        get => _transport;
        init
        {
            _transport = value;
            _transportWasSet = true;
        }
    }
    public CacheRetention CacheRetention
    {
        get => _cacheRetention;
        init
        {
            _cacheRetention = value;
            _cacheRetentionWasSet = true;
        }
    }
    public string? SessionId { get; init; }
    public IDictionary<string, string>? Headers { get; init; }
    public TimeSpan? MaxRetryDelay { get; init; }
    public IDictionary<string, object>? Metadata { get; init; }

    internal bool HasExplicitTransport => _transportWasSet;
    internal bool HasExplicitCacheRetention => _cacheRetentionWasSet;
}

public record SimpleStreamOptions : StreamOptions
{
    public ThinkingLevel? Reasoning { get; init; }
    public ThinkingBudgets? ThinkingBudgets { get; init; }
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
