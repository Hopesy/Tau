using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.AgentCore.Harness.Session;

public sealed class AgentHarnessSession<TMetadata>
    where TMetadata : SessionMetadata
{
    private readonly ISessionStorage<TMetadata> _storage;

    public AgentHarnessSession(ISessionStorage<TMetadata> storage)
    {
        _storage = storage;
    }

    public Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) =>
        _storage.GetMetadataAsync(cancellationToken);

    public ISessionStorage<TMetadata> GetStorage() => _storage;

    public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) =>
        _storage.GetLeafIdAsync(cancellationToken);

    public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) =>
        _storage.GetEntryAsync(id, cancellationToken);

    public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) =>
        _storage.GetEntriesAsync(cancellationToken);

    /// <summary>按 entry 查询条件读取 session 范围内的 entries。</summary>
    /// <param name="query">类型、顺序、游标和数量过滤。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的 entries。</returns>
    public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(
        EntryQuery? query = null,
        CancellationToken cancellationToken = default) =>
        _storage.FindEntriesAsync(query, cancellationToken);

    /// <summary>读取指定分支上的 entries，支持 stopAt 边界。</summary>
    /// <param name="query">entry 过滤和顺序。</param>
    /// <param name="bounds">起点、类型边界和 id 边界。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分支 entries。</returns>
    public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesOnBranchAsync(
        EntryQuery? query = null,
        BranchBounds? bounds = null,
        CancellationToken cancellationToken = default) =>
        _storage.FindEntriesOnBranchAsync(query, bounds, cancellationToken);

    /// <summary>查询 session 中的 lane records。</summary>
    /// <param name="query">record 查询条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的 lane records。</returns>
    public Task<IReadOnlyList<LaneRecord>> FindRecordsAsync(
        RecordQuery? query = null,
        CancellationToken cancellationToken = default) =>
        _storage.FindRecordsAsync(query, cancellationToken);

    /// <summary>读取 session 当前 lane 指针。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>lane 指针列表。</returns>
    public Task<IReadOnlyList<LanePointer>> GetLanesAsync(CancellationToken cancellationToken = default) =>
        _storage.GetLanesAsync(cancellationToken);

    /// <summary>创建一个新的 session lane。</summary>
    /// <param name="lane">lane 名称。</param>
    /// <param name="at">初始叶节点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task CreateLaneAsync(string lane, string? at = null, CancellationToken cancellationToken = default) =>
        _storage.CreateLaneAsync(lane, at, cancellationToken);

    /// <summary>移动 session lane 的叶节点。</summary>
    /// <param name="lane">lane 名称。</param>
    /// <param name="to">目标叶节点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task MoveLaneAsync(string lane, string? to, CancellationToken cancellationToken = default) =>
        _storage.MoveLaneAsync(lane, to, cancellationToken);

    /// <summary>读取全局 session 名称。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前名称。</returns>
    public Task<string?> GetNameAsync(CancellationToken cancellationToken = default) =>
        _storage.GetNameAsync(cancellationToken);

    /// <summary>设置全局 session 名称。</summary>
    /// <param name="name">名称；空值清除。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task SetNameAsync(string? name, CancellationToken cancellationToken = default) =>
        _storage.SetNameAsync(name, cancellationToken);

    /// <summary>读取 session 统计。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统计快照。</returns>
    public Task<SessionStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        _storage.GetStatsAsync(cancellationToken);

    /// <summary>设置或清除 entry 的全局 label。</summary>
    /// <param name="id">目标 entry id。</param>
    /// <param name="label">label 文本；空值清除。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task SetLabelAsync(string id, string? label, CancellationToken cancellationToken = default) =>
        _storage.SetLabelAsync(id, label, cancellationToken);

    /// <summary>读取 entry、record、lane 和 fact 的合并顺序日志。</summary>
    /// <param name="afterSequence">独占序号游标。</param>
    /// <param name="limit">最多返回数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>顺序日志项。</returns>
    public Task<IReadOnlyList<SessionLogItem>> GetLogAsync(long? afterSequence = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _storage.GetLogAsync(afterSequence, limit, cancellationToken);

    /// <summary>查询尚未结束的 lane 操作。</summary>
    /// <param name="lane">lane 名称。</param>
    /// <param name="limit">最多返回数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>未结束操作。</returns>
    public Task<IReadOnlyList<OperationStartedRecord>> FindOpenOperationsAsync(
        string lane,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        _storage.FindOpenOperationsAsync(lane, limit, cancellationToken);

    public async Task<IReadOnlyList<SessionTreeEntry>> GetBranchAsync(
        string? fromId = null,
        CancellationToken cancellationToken = default)
    {
        var leafId = fromId ?? await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false);
        return await _storage.GetPathToRootAsync(leafId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionContext> BuildContextAsync(CancellationToken cancellationToken = default) =>
        BuildSessionContext(await GetBranchAsync(cancellationToken: cancellationToken).ConfigureAwait(false));

    public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default) =>
        _storage.GetLabelAsync(id, cancellationToken);

    public async Task<string?> GetSessionNameAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _storage.FindEntriesAsync("session_info", cancellationToken).ConfigureAwait(false);
        return entries
            .OfType<SessionInfoEntry>()
            .LastOrDefault()
            ?.Name
            ?.Trim() is { Length: > 0 } name
                ? name
                : null;
    }

    public async Task<string> AppendMessageAsync(
        ChatMessage message,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new MessageSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                message),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendThinkingLevelChangeAsync(
        string thinkingLevel,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new ThinkingLevelChangeSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                thinkingLevel),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendModelChangeAsync(
        string provider,
        string modelId,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new ModelChangeSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                provider,
                modelId),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendActiveToolsChangeAsync(
        IReadOnlyList<string> activeToolNames,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new ActiveToolsChangeSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                activeToolNames.ToArray()),
            cancellationToken).ConfigureAwait(false);

    /// <summary>【AgentCore】【压缩基线】保存摘要及当前分支重放后的系统声明。</summary>
    /// <param name="summary">压缩摘要。</param>
    /// <param name="firstKeptEntryId">保留尾部的起点；不匹配时不保留旧消息。</param>
    /// <param name="tokensBefore">压缩前的 token 数。</param>
    /// <param name="details">压缩附加信息。</param>
    /// <param name="fromHook">是否由扩展提供摘要。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新压缩条目的标识。</returns>
    public async Task<string> AppendCompactionAsync(
        string summary,
        string firstKeptEntryId,
        int tokensBefore,
        object? details = null,
        bool fromHook = false,
        CancellationToken cancellationToken = default)
    {
        var context = await BuildContextAsync(cancellationToken).ConfigureAwait(false);
        var system = Transcript.GetCurrentSystemMessage(context.Messages);
        var timestamp = DateTimeOffset.UtcNow;
        return await AppendTypedEntryAsync(
            new CompactionSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                timestamp,
                summary,
                firstKeptEntryId,
                tokensBefore,
                details,
                fromHook)
            {
                SystemMessage = system is null ? null : system with { Timestamp = timestamp }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> AppendCustomEntryAsync(
        string customType,
        object? data = null,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new CustomSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                customType,
                data),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendCustomMessageEntryAsync(
        string customType,
        string content,
        bool display,
        object? details = null,
        CancellationToken cancellationToken = default) =>
        await AppendCustomMessageEntryAsync(
            customType,
            [new TextContent(content)],
            display,
            details,
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendCustomMessageEntryAsync(
        string customType,
        IReadOnlyList<ContentBlock> content,
        bool display,
        object? details = null,
        CancellationToken cancellationToken = default) =>
        await AppendTypedEntryAsync(
            new CustomMessageSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                customType,
                content.ToArray(),
                display,
                details),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> AppendLabelAsync(
        string targetId,
        string? label,
        CancellationToken cancellationToken = default)
    {
        if (await _storage.GetEntryAsync(targetId, cancellationToken).ConfigureAwait(false) is null)
            throw new SessionException("not_found", $"Entry {targetId} not found");

        return await AppendTypedEntryAsync(
            new LabelSessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                targetId,
                label),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> AppendSessionNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var sanitized = name
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return await AppendTypedEntryAsync(
            new SessionInfoEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                DateTimeOffset.UtcNow,
                sanitized),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> MoveToAsync(
        string? entryId,
        SessionBranchSummary? summary = null,
        CancellationToken cancellationToken = default)
    {
        if (entryId is not null && await _storage.GetEntryAsync(entryId, cancellationToken).ConfigureAwait(false) is null)
            throw new SessionException("not_found", $"Entry {entryId} not found");

        await _storage.SetLeafIdAsync(entryId, cancellationToken).ConfigureAwait(false);
        if (summary is null)
            return null;

        return await AppendTypedEntryAsync(
            new BranchSummarySessionEntry(
                await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
                entryId,
                DateTimeOffset.UtcNow,
                entryId ?? "root",
                summary.Summary,
                summary.Details,
                summary.FromHook),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>【AgentCore】【会话恢复】投影当前分支，以最新压缩基线替代压缩前的系统增量。</summary>
    /// <param name="pathEntries">从根到当前叶节点的条目。</param>
    /// <returns>恢复后的消息、模型、推理等级和工具选择。</returns>
    public static SessionContext BuildSessionContext(IReadOnlyList<SessionTreeEntry> pathEntries)
    {
        var thinkingLevel = "off";
        SessionModelReference? model = null;
        IReadOnlyList<string>? activeToolNames = null;
        CompactionSessionEntry? compaction = null;

        foreach (var entry in pathEntries)
        {
            switch (entry)
            {
                case ThinkingLevelChangeSessionEntry thinking:
                    thinkingLevel = thinking.ThinkingLevel;
                    break;
                case ModelChangeSessionEntry modelChange:
                    model = new SessionModelReference(modelChange.Provider, modelChange.ModelId);
                    break;
                case ActiveToolsChangeSessionEntry tools:
                    activeToolNames = tools.ActiveToolNames.ToArray();
                    break;
                case CompactionSessionEntry compactionEntry:
                    compaction = compactionEntry;
                    break;
                case MessageSessionEntry { Message: AssistantMessage { Provider: not null, Model: not null } assistant }:
                    model = new SessionModelReference(assistant.Provider, assistant.Model);
                    break;
            }
        }

        var messages = new List<ChatMessage>();
        void AppendMessage(SessionTreeEntry entry)
        {
            switch (entry)
            {
                case MessageSessionEntry message:
                    messages.Add(message.Message);
                    break;
                case CustomMessageSessionEntry custom:
                    messages.Add(AgentHarnessMessages.CreateCustomMessage(
                        custom.CustomType,
                        custom.Content,
                        custom.Display,
                        custom.Details,
                        custom.Timestamp));
                    break;
                case BranchSummarySessionEntry { Summary.Length: > 0 } summary:
                    messages.Add(AgentHarnessMessages.CreateBranchSummaryMessage(
                        summary.Summary,
                        summary.FromId,
                        summary.Timestamp));
                    break;
            }
        }

        if (compaction is not null)
        {
            // 1. 【AgentCore】【压缩基线】先恢复有效声明，再恢复摘要与普通尾部，避免重复应用旧增量
            if (compaction.SystemMessage is not null) messages.Add(compaction.SystemMessage);
            messages.Add(AgentHarnessMessages.CreateCompactionSummaryMessage(
                compaction.Summary,
                compaction.TokensBefore,
                compaction.Timestamp));
            var compactionIndex = pathEntries
                .Select((entry, index) => (entry, index))
                .FirstOrDefault(pair => pair.entry is CompactionSessionEntry current && current.Id == compaction.Id)
                .index;
            var foundFirstKept = false;
            for (var i = 0; i < compactionIndex; i++)
            {
                var entry = pathEntries[i];
                if (entry.Id == compaction.FirstKeptEntryId)
                    foundFirstKept = true;
                if (foundFirstKept && entry is not MessageSessionEntry { Message: SystemMessage })
                    AppendMessage(entry);
            }

            for (var i = compactionIndex + 1; i < pathEntries.Count; i++)
            {
                AppendMessage(pathEntries[i]);
            }
        }
        else
        {
            foreach (var entry in pathEntries)
            {
                AppendMessage(entry);
            }
        }

        return new SessionContext(messages, thinkingLevel, model, activeToolNames);
    }

    private async Task<string> AppendTypedEntryAsync<TEntry>(
        TEntry entry,
        CancellationToken cancellationToken)
        where TEntry : SessionTreeEntry
    {
        await _storage.AppendEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry.Id;
    }
}
