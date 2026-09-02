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
    public TimeSpan? Timeout { get; init; }
    public TimeSpan? MaxRetryDelay { get; init; }
    public int? MaxRetries { get; init; }
    public TimeSpan? WebSocketConnectTimeout { get; init; }
    public IDictionary<string, object>? Metadata { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    /// <summary>发送给兼容 provider 的额外采样参数。</summary>
    public IReadOnlyDictionary<string, object>? SamplingParams { get; init; }
    /// <summary>请求异步 deferred 响应；可使用 true 或包含 window 的配置对象。</summary>
    public object? Deferred { get; init; }

    internal bool HasExplicitTransport => _transportWasSet;
    internal bool HasExplicitCacheRetention => _cacheRetentionWasSet;
}

public record SimpleStreamOptions : StreamOptions
{
    public ThinkingLevel? Reasoning { get; init; }
    public ThinkingBudgets? ThinkingBudgets { get; init; }
    /// <summary>
    /// provider 无关的工具选择策略；可使用 auto、none、required 或 provider 兼容的对象。
    /// </summary>
    public object? ToolChoice { get; init; }
}

/// <summary>
/// deferred 响应拉取选项。
/// </summary>
public record DeferredFetchOptions : StreamOptions
{
    /// <summary>provider 长轮询等待时长；为空时由 provider 决定。</summary>
    public TimeSpan? Wait { get; init; }
}

/// <summary>
/// deferred 响应取消选项。
/// </summary>
public record DeferredCancelOptions : StreamOptions;

/// <summary>
/// pi-messages 协议专用的流式选项，字段名与参考项目保持一致。
/// </summary>
public record PiMessagesOptions : SimpleStreamOptions
{
    /// <summary>是否在请求 URL 上附加 debug=1。</summary>
    public bool Debug { get; init; }
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
    Off,
    Minimal,
    Low,
    Medium,
    High,
    ExtraHigh
}
