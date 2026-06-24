using Tau.Ai;

namespace Tau.AgentCore.Harness.Session;

public record SessionMetadata(string Id, string CreatedAt);

public record JsonlSessionMetadata(
    string Id,
    string CreatedAt,
    string Cwd,
    string Path,
    string? ParentSessionPath = null) : SessionMetadata(Id, CreatedAt);

public sealed record SessionModelReference(string Provider, string ModelId);

public sealed record SessionContext(
    IReadOnlyList<ChatMessage> Messages,
    string ThinkingLevel,
    SessionModelReference? Model,
    IReadOnlyList<string>? ActiveToolNames);

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
    DateTimeOffset Timestamp);

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

public interface ISessionStorage<TMetadata>
    where TMetadata : SessionMetadata
{
    Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default);
    Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default);
    Task SetLeafIdAsync(string? leafId, CancellationToken cancellationToken = default);
    Task<string> CreateEntryIdAsync(CancellationToken cancellationToken = default);
    Task AppendEntryAsync(SessionTreeEntry entry, CancellationToken cancellationToken = default);
    Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(string type, CancellationToken cancellationToken = default);
    Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionTreeEntry>> GetPathToRootAsync(string? leafId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);
}

public sealed record SessionForkOptions(
    string? EntryId = null,
    string Position = "before",
    string? Id = null);
