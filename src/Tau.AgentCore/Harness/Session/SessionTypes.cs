using System.Text.Json;
using Tau.Ai;

namespace Tau.AgentCore.Harness.Session;

public record SessionMetadata(string Id, string CreatedAt);

public record JsonlSessionMetadata(
    string Id,
    string CreatedAt,
    string Cwd,
    string Path,
    string? ParentSessionPath = null) : SessionMetadata(Id, CreatedAt)
{
    /// <summary>文件最后修改时间（Unix 毫秒）。</summary>
    public long ModifiedAt { get; init; }

    /// <summary>源文件格式版本，3 表示旧 tree JSONL，4 表示 lane record-log JSONL。</summary>
    public int SourceFormat { get; init; } = 3;

    /// <summary>v4 header 中的父 session id。</summary>
    public string? ParentSessionId { get; init; }

    /// <summary>v3 父 session 路径无法解析时保留的原始路径。</summary>
    public string? LegacyParentSessionPath { get; init; }

    /// <summary>v4 header 中的应用自定义元数据。</summary>
    public JsonElement? Metadata { get; init; }
}

public sealed record SessionModelReference(string Provider, string ModelId);

public sealed record SessionContext(
    IReadOnlyList<ChatMessage> Messages,
    string ThinkingLevel,
    SessionModelReference? Model,
    IReadOnlyList<string>? ActiveToolNames);

/// <summary>表示一个 session lane 及其当前叶节点。</summary>
/// <param name="Lane">lane 名称。</param>
/// <param name="LeafId">lane 当前叶 entry id；空值表示尚未追加 entry。</param>
public sealed record LanePointer(string Lane, string? LeafId);

/// <summary>session 统计信息。</summary>
/// <param name="MessageCount">message entry 数量。</param>
/// <param name="CachedTokens">缓存命中的 token 数。</param>
/// <param name="UncachedTokens">未命中缓存的 token 数。</param>
/// <param name="TotalTokens">总 token 数。</param>
/// <param name="CostTotal">累计成本。</param>
public sealed record SessionStats(
    int MessageCount,
    int CachedTokens,
    int UncachedTokens,
    int TotalTokens,
    decimal CostTotal);

/// <summary>session 顺序日志中的一条 entry 项。</summary>
public sealed record EntryLogItem(long Sequence, SessionTreeEntry Entry);

/// <summary>session 顺序日志中的一条 record 项。</summary>
public sealed record RecordLogItem(long Sequence, LaneRecord Record);

/// <summary>session 顺序日志中的一条 lane 指针变更项。</summary>
public sealed record LaneLogItem(long Sequence, string Lane, string? LeafId);

/// <summary>session 顺序日志中的一条全局 fact 变更项。</summary>
public sealed record FactLogItem(long Sequence, string Fact, string? TargetId, string? Value);

/// <summary>统一的 session 顺序日志项。</summary>
public abstract record SessionLogItem(long Sequence, string Kind);

/// <summary>entry 类型的顺序日志项。</summary>
public sealed record SessionEntryLogItem(long Sequence, SessionTreeEntry Entry)
    : SessionLogItem(Sequence, "entry");

/// <summary>record 类型的顺序日志项。</summary>
public sealed record SessionRecordLogItem(long Sequence, LaneRecord Record)
    : SessionLogItem(Sequence, "record");

/// <summary>lane 类型的顺序日志项。</summary>
public sealed record SessionLaneLogItem(long Sequence, string Lane, string? LeafId)
    : SessionLogItem(Sequence, "lane");

/// <summary>fact 类型的顺序日志项。</summary>
public sealed record SessionFactLogItem(long Sequence, string Fact, string? TargetId, string? Value)
    : SessionLogItem(Sequence, "fact");

public sealed class SessionException : Exception
{
    public SessionException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public abstract record SessionTreeEntry(
    string Type,
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp)
{
    /// <summary>session 全局递增序号；v3 文件缺失时由存储按读取顺序补齐。</summary>
    public long Sequence { get; init; }

    /// <summary>v4 entry 所属 lane；旧版 tree entry 没有该字段。</summary>
    public string? Lane { get; init; }
}

public sealed record MessageSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    ChatMessage Message) : SessionTreeEntry("message", Id, ParentId, Timestamp);

public sealed record ThinkingLevelChangeSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string ThinkingLevel) : SessionTreeEntry("thinking_level_change", Id, ParentId, Timestamp);

public sealed record ModelChangeSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string Provider,
    string ModelId) : SessionTreeEntry("model_change", Id, ParentId, Timestamp);

public sealed record ActiveToolsChangeSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    IReadOnlyList<string> ActiveToolNames) : SessionTreeEntry("active_tools_change", Id, ParentId, Timestamp);

public sealed record CompactionSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string Summary,
    string FirstKeptEntryId,
    int TokensBefore,
    object? Details = null,
    bool FromHook = false) : SessionTreeEntry("compaction", Id, ParentId, Timestamp);

public sealed record CustomSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string CustomType,
    object? Data = null) : SessionTreeEntry("custom", Id, ParentId, Timestamp);

public sealed record CustomMessageSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string CustomType,
    IReadOnlyList<ContentBlock> Content,
    bool Display,
    object? Details = null) : SessionTreeEntry("custom_message", Id, ParentId, Timestamp);

public sealed record LabelSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string TargetId,
    string? Label) : SessionTreeEntry("label", Id, ParentId, Timestamp);

public sealed record SessionInfoEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string? Name) : SessionTreeEntry("session_info", Id, ParentId, Timestamp);

public sealed record LeafSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string? TargetId) : SessionTreeEntry("leaf", Id, ParentId, Timestamp);

public sealed record BranchSummarySessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string FromId,
    string Summary,
    object? Details = null,
    bool FromHook = false) : SessionTreeEntry("branch_summary", Id, ParentId, Timestamp);

public sealed record SessionBranchSummary(
    string Summary,
    object? Details = null,
    bool FromHook = false);

/// <summary>查询结果的时间顺序。</summary>
public enum EntryOrder
{
    /// <summary>从最新记录向最旧记录返回。</summary>
    NewestFirst,
    /// <summary>从最旧记录向最新记录返回。</summary>
    OldestFirst
}

/// <summary>session entry 的过滤条件。</summary>
/// <param name="Type">可选 entry 类型，例如 message 或 custom。</param>
/// <param name="CustomType">custom entry 的具体类型过滤。</param>
/// <param name="Order">返回顺序，默认最新优先。</param>
/// <param name="Limit">最多返回数量，必须为正数。</param>
/// <param name="AfterSequence">独占序号游标；内存/JSONL 存储按 entry 写入顺序编号。</param>
public sealed record EntryQuery(
    string? Type = null,
    string? CustomType = null,
    EntryOrder Order = EntryOrder.NewestFirst,
    int? Limit = null,
    long? AfterSequence = null)
{
    /// <summary>与 pi afterSeq 同名的兼容别名。</summary>
    public long? AfterSeq => AfterSequence;
}

/// <summary>分支查询的起止边界。</summary>
/// <param name="StartId">起始 entry；为空时使用当前 leaf。</param>
/// <param name="StopAtType">命中该类型后停止，命中项仍包含在结果中。</param>
/// <param name="StopAtId">命中该 id 后停止，命中项仍包含在结果中。</param>
public sealed record BranchBounds(
    string? StartId = null,
    string? StopAtType = null,
    string? StopAtId = null)
{
    /// <summary>与 pi start 同名的兼容别名。</summary>
    public string? Start => StartId;
}

/// <summary>Harness lane record 的过滤条件。</summary>
/// <param name="Lane">精确 lane 过滤。</param>
/// <param name="Type">精确 record 类型过滤。</param>
/// <param name="RunId">操作 id 过滤；operation_started 使用自身 id，其余 record 使用 RunId。</param>
/// <param name="OperationKind">仅 operation_started 支持的操作意图类型。</param>
/// <param name="AfterSequence">独占序号游标。</param>
/// <param name="Order">返回顺序，默认最新优先。</param>
/// <param name="Limit">最多返回数量，必须为正数。</param>
public sealed record RecordQuery(
    string? Lane = null,
    string? Type = null,
    string? RunId = null,
    string? OperationKind = null,
    long? AfterSequence = null,
    EntryOrder Order = EntryOrder.NewestFirst,
    int? Limit = null)
{
    /// <summary>与 pi afterSeq 同名的兼容别名。</summary>
    public long? AfterSeq => AfterSequence;
}

public interface ISessionStorage<TMetadata>
    where TMetadata : SessionMetadata
{
    Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default);
    Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default);
    Task SetLeafIdAsync(string? leafId, CancellationToken cancellationToken = default);
    Task<string> CreateEntryIdAsync(CancellationToken cancellationToken = default);
    Task AppendEntryAsync(SessionTreeEntry entry, CancellationToken cancellationToken = default);
    /// <summary>追加一条 lane record；JSONL 旧格式默认只保留内存态记录。</summary>
    Task AppendRecordAsync(LaneRecord record, CancellationToken cancellationToken = default);
    /// <summary>读取当前 session 的所有 lane 指针。</summary>
    Task<IReadOnlyList<LanePointer>> GetLanesAsync(CancellationToken cancellationToken = default);
    /// <summary>创建一个新的 lane，并把其叶节点定位到指定 entry。</summary>
    Task CreateLaneAsync(string lane, string? at = null, CancellationToken cancellationToken = default);
    /// <summary>移动已有 lane 的叶节点。</summary>
    Task MoveLaneAsync(string lane, string? to, CancellationToken cancellationToken = default);
    Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(string type, CancellationToken cancellationToken = default);
    /// <summary>按类型、顺序、游标和数量查询 session entries。</summary>
    Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(EntryQuery? query, CancellationToken cancellationToken = default);
    /// <summary>沿 entry 父链查询分支，结果默认从 leaf 向 root 返回。</summary>
    Task<IReadOnlyList<SessionTreeEntry>> FindEntriesOnBranchAsync(EntryQuery? query = null, BranchBounds? bounds = null, CancellationToken cancellationToken = default);
    /// <summary>查询持久化 lane records；旧版 tree-only 存储没有 records 时返回空列表。</summary>
    Task<IReadOnlyList<LaneRecord>> FindRecordsAsync(RecordQuery? query = null, CancellationToken cancellationToken = default);
    /// <summary>查询尚未结束的操作，结果按开始序号从新到旧排列。</summary>
    Task<IReadOnlyList<OperationStartedRecord>> FindOpenOperationsAsync(string lane, int? limit = null, CancellationToken cancellationToken = default);
    Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionTreeEntry>> GetPathToRootAsync(string? leafId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);
    /// <summary>读取全局 session 名称。</summary>
    Task<string?> GetNameAsync(CancellationToken cancellationToken = default);
    /// <summary>设置全局 session 名称；传入空值清除名称。</summary>
    Task SetNameAsync(string? name, CancellationToken cancellationToken = default);
    /// <summary>读取 session 统计信息。</summary>
    Task<SessionStats> GetStatsAsync(CancellationToken cancellationToken = default);
    /// <summary>设置或清除 entry 的全局 label。</summary>
    Task SetLabelAsync(string id, string? label, CancellationToken cancellationToken = default);
    /// <summary>按共享序号读取 entry、record、lane 和 fact 的合并日志。</summary>
    Task<IReadOnlyList<SessionLogItem>> GetLogAsync(long? afterSequence = null, int? limit = null, CancellationToken cancellationToken = default);
}

public sealed record SessionForkOptions(
    string? EntryId = null,
    string Position = "before",
    string? Id = null);
