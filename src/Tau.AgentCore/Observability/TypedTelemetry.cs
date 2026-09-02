namespace Tau.AgentCore.Observability;

/// <summary>通用 telemetry span 状态。</summary>
public sealed record TelemetrySpan(string Name, IReadOnlyDictionary<string, object?> Attributes, DateTimeOffset StartedAt, DateTimeOffset? EndedAt = null, string? Error = null);

/// <summary>可注入的 telemetry 上下文。</summary>
public interface ITelemetryContext
{
    /// <summary>启动一个 span 并执行异步委托。</summary>
    /// <typeparam name="T">委托结果类型。</typeparam>
    /// <param name="name">span 名称。</param>
    /// <param name="attributes">span 属性。</param>
    /// <param name="operation">要执行的委托。</param>
    /// <returns>委托结果。</returns>
    Task<T> StartSpanAsync<T>(string name, IReadOnlyDictionary<string, object?>? attributes, Func<TelemetrySpan, Task<T>> operation);
}

/// <summary>将 span 保存在内存中的 telemetry 上下文。</summary>
public sealed class InMemoryTelemetryContext : ITelemetryContext
{
    private readonly List<TelemetrySpan> _spans = [];
    /// <summary>已结束和正在执行的 span 快照。</summary>
    public IReadOnlyList<TelemetrySpan> Spans => _spans;
    /// <inheritdoc />
    public async Task<T> StartSpanAsync<T>(string name, IReadOnlyDictionary<string, object?>? attributes, Func<TelemetrySpan, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var span = new TelemetrySpan(name, attributes ?? new Dictionary<string, object?>(), DateTimeOffset.UtcNow);
        lock (_spans) _spans.Add(span);
        try { return await operation(span).ConfigureAwait(false); }
        catch (Exception ex) { Update(span with { EndedAt = DateTimeOffset.UtcNow, Error = ex.Message }); throw; }
        finally { Update(span with { EndedAt = DateTimeOffset.UtcNow }); }
    }
    private void Update(TelemetrySpan span) { lock (_spans) { var index = _spans.FindIndex(item => item.StartedAt == span.StartedAt && item.Name == span.Name); if (index >= 0) _spans[index] = span; } }
}

/// <summary>不产生任何记录的 telemetry 上下文。</summary>
public sealed class NoopTelemetryContext : ITelemetryContext
{
    /// <summary>全局空实现。</summary>
    public static NoopTelemetryContext Instance { get; } = new();
    /// <inheritdoc />
    public Task<T> StartSpanAsync<T>(string name, IReadOnlyDictionary<string, object?>? attributes, Func<TelemetrySpan, Task<T>> operation) => operation(new TelemetrySpan(name, attributes ?? new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
}

/// <summary>Agent telemetry schema 和 span 工厂。</summary>
public static class AgentTelemetry
{
    /// <summary>AI span schema 名称。</summary>
    public const string AiSchema = "pi.ai";
    /// <summary>Harness span schema 名称。</summary>
    public const string HarnessSchema = "pi.harness";
    /// <summary>Schema 版本。</summary>
    public const int SchemaVersion = 1;
    /// <summary>构造 pi.ai.request 的标准起始属性。</summary>
    /// <param name="operation">逻辑操作名。</param>
    /// <param name="provider">provider id。</param>
    /// <param name="model">模型 id。</param>
    /// <param name="api">API id。</param>
    /// <param name="streaming">是否返回流。</param>
    /// <param name="deferred">是否延迟执行。</param>
    /// <returns>标准属性字典。</returns>
    public static IReadOnlyDictionary<string, object?> CreateAiRequestAttributes(string operation, string provider, string model, string api, bool streaming, bool deferred = false) => new Dictionary<string, object?>
    {
        ["pi.ai.operation"] = operation,
        ["pi.ai.provider"] = provider,
        ["pi.ai.model"] = model,
        ["pi.ai.api"] = api,
        ["pi.ai.streaming"] = streaming,
        ["pi.ai.deferred"] = deferred
    };

    /// <summary>构造 pi.harness.* 操作的通用起始属性。</summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="lane">lane 名称。</param>
    /// <param name="operationId">持久化操作 id。</param>
    /// <param name="operationKind">run、compaction 或 navigation。</param>
    /// <param name="recovery">是否恢复旧操作。</param>
    /// <returns>标准属性字典。</returns>
    public static IReadOnlyDictionary<string, object?> CreateHarnessOperationAttributes(string sessionId, string lane, string operationId, string operationKind, bool recovery) => new Dictionary<string, object?>
    {
        ["pi.session.id"] = sessionId,
        ["pi.lane.name"] = lane,
        ["pi.operation.id"] = operationId,
        ["pi.operation.recovery"] = recovery,
        ["pi.operation.kind"] = operationKind
    };
    /// <summary>启动 AI span。</summary>
    public static Task<T> StartAiSpanAsync<T>(ITelemetryContext context, string name, IReadOnlyDictionary<string, object?>? attributes, Func<TelemetrySpan, Task<T>> operation) => context.StartSpanAsync($"{AiSchema}.{name}", attributes, operation);
    /// <summary>启动 Harness span。</summary>
    public static Task<T> StartHarnessSpanAsync<T>(ITelemetryContext context, string name, IReadOnlyDictionary<string, object?>? attributes, Func<TelemetrySpan, Task<T>> operation) => context.StartSpanAsync($"{HarnessSchema}.{name}", attributes, operation);
}
