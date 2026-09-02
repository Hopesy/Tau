namespace Tau.Ai;

/// <summary>
/// Base type for all chat messages. Pattern match on subtypes for exhaustive handling.
/// </summary>
public abstract record ChatMessage(string Role);

public sealed record UserMessage : ChatMessage
{
    public UserMessage(string text) : base("user") => Content = [new TextContent(text)];
    public UserMessage(IReadOnlyList<ContentBlock> content) : base("user") => Content = content;
    public IReadOnlyList<ContentBlock> Content { get; init; }
    /// <summary>消息创建时间，未提供时为空。</summary>
    public DateTimeOffset? Timestamp { get; init; }
}

public sealed record AssistantMessage : ChatMessage
{
    public AssistantMessage(IReadOnlyList<ContentBlock> content) : base("assistant") => Content = content;
    public AssistantMessage() : base("assistant") => Content = [];

    public IReadOnlyList<ContentBlock> Content { get; init; }
    public Usage? Usage { get; init; }
    public StopReason? StopReason { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<AssistantMessageDiagnostic>? Diagnostics { get; init; }
    public string? Api { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? ResponseId { get; init; }
    /// <summary>provider 返回的具体响应模型名称。</summary>
    public string? ResponseModel { get; init; }
    /// <summary>provider 原始停止原因，便于诊断。</summary>
    public string? RawStopReason { get; init; }
    /// <summary>provider 是否明确报告了 end-turn。</summary>
    public bool? EndTurn { get; init; }
    /// <summary>异步 deferred 响应句柄。</summary>
    public DeferredHandle? Deferred { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
}

public sealed record ToolResultMessage(
    string ToolCallId,
    IReadOnlyList<ContentBlock> Content,
    bool IsError = false) : ChatMessage("toolResult")
{
    public string? ToolName { get; init; }
    /// <summary>工具执行的结构化详情。</summary>
    public object? Details { get; init; }
    /// <summary>工具执行阶段产生的用量。</summary>
    public Usage? Usage { get; init; }
    /// <summary>本次结果使 provider 新增可用的工具名称。</summary>
    public IReadOnlyList<string>? AddedToolNames { get; init; }
    /// <summary>工具结果创建时间。</summary>
    public DateTimeOffset? Timestamp { get; init; }
}

public record struct Usage(
    int InputTokens,
    int OutputTokens,
    int? CacheReadTokens = null,
    int? CacheWriteTokens = null,
    string? ServiceTier = null,
    UsageCost? Cost = null)
{
    /// <summary>Anthropic 等 provider 报告的 1 小时缓存写入 token 子集。</summary>
    public int? CacheWrite1hTokens { get; init; }
    /// <summary>服务端报告的推理 token 数，未提供时为空。</summary>
    public int? ReasoningTokens { get; init; }

    /// <summary>服务端报告的总 token 数，未提供时为空。</summary>
    public int? TotalTokens { get; init; }

    /// <summary>
    /// 兼容旧版 Usage 值比较，仅比较原有计费维度；推理和总量属于附加诊断字段。
    /// </summary>
    /// <param name="other">待比较的用量。</param>
    /// <returns>原有计费维度全部相同时返回 true。</returns>
    public bool Equals(Usage other) =>
        InputTokens == other.InputTokens &&
        OutputTokens == other.OutputTokens &&
        CacheReadTokens == other.CacheReadTokens &&
        CacheWriteTokens == other.CacheWriteTokens &&
        ServiceTier == other.ServiceTier &&
        EqualityComparer<UsageCost?>.Default.Equals(Cost, other.Cost);

    /// <summary>按原有计费维度生成兼容哈希值。</summary>
    /// <returns>用量哈希值。</returns>
    public override int GetHashCode() => HashCode.Combine(
        InputTokens,
        OutputTokens,
        CacheReadTokens,
        CacheWriteTokens,
        ServiceTier,
        Cost);
}

/// <summary>
/// provider 异步响应句柄，用于后续轮询或取消 deferred 请求。
/// </summary>
/// <param name="Provider">provider 标识。</param>
/// <param name="ModelId">模型标识。</param>
/// <param name="Api">使用的 API 协议。</param>
/// <param name="Id">provider 返回的句柄。</param>
public sealed record DeferredHandle(string Provider, string ModelId, string Api, string Id)
{
    /// <summary>句柄过期时间。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>建议下一次轮询前等待的毫秒数。</summary>
    public int? PollAfterMs { get; init; }
    /// <summary>provider 轮询所需的附加数据。</summary>
    public object? Data { get; init; }
}

public enum StopReason
{
    EndTurn,
    MaxTokens,
    ToolUse,
    ContentFilter,
    Error,
    Aborted,
    Deferred
}
