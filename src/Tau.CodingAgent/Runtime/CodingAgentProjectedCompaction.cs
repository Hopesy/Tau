// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【可见压缩】按照编辑后的上下文投影选取摘要内容和保留边界。</summary>
internal static class CodingAgentProjectedCompaction
{
    /// <summary>【CodingAgent】【压缩准备】仅摘要当前分支可见且位于保留窗口之前的对话。</summary>
    /// <param name="branch">从根到叶节点的原始分支。</param>
    /// <param name="settings">保留窗口与摘要预算。</param>
    /// <returns>压缩准备；没有可摘要内容时为空。</returns>
    internal static AgentCompactionPreparation? Prepare(IReadOnlyList<CodingAgentTreeSessionEntry> branch, AgentCompactionSettings settings)
    {
        if (branch.LastOrDefault()?.Type == "compaction") return null;
        var projection = CodingAgentTreeSessionStore.ProjectBranch(branch);
        var entries = projection.Entries;
        var sources = entries.Select(entry => JsonSerializer.Deserialize(entry.SourceEntry,
            CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry)!).ToArray();
        var previous = Enumerable.Range(0, entries.Count).FirstOrDefault(index => sources[index].Type == "compaction" && entries[index].Messages.Count > 0, -1);
        var start = previous + 1;
        var cut = FindCutPoint(entries, sources, start, settings.KeepRecentTokens);
        if (cut.FirstKeptEntryIndex >= entries.Count) return null;
        var historyEnd = cut.IsSplitTurn ? cut.TurnStartIndex : cut.FirstKeptEntryIndex;
        var history = CollectMessages(entries, sources, start, historyEnd);
        var prefix = cut.IsSplitTurn ? CollectMessages(entries, sources, cut.TurnStartIndex, cut.FirstKeptEntryIndex) : [];
        if (history.Count == 0 && prefix.Count == 0) return null;
        // 1. 【CodingAgent】【文件轨迹】沿用原生摘要中的文件记录，并读取编辑后的工具调用
        var fileOps = AgentCompaction.CreateFileOperations();
        if (previous >= 0 && sources[previous] is { FromHook: not true, Details: { ValueKind: JsonValueKind.Object } details })
        {
            CopyPaths(details, "readFiles", fileOps.Read);
            CopyPaths(details, "modifiedFiles", fileOps.Edited);
        }
        foreach (var message in history.Concat(prefix)) AgentCompaction.ExtractFileOperationsFromMessage(message, fileOps);
        return new(sources[cut.FirstKeptEntryIndex].Id, history, prefix, cut.IsSplitTurn,
            EstimateContextUsage(projection, branch).Tokens,
            previous >= 0 ? sources[previous].Summary : null, fileOps, settings);
    }

    /// <summary>【CodingAgent】【投影用量】上下文编辑或压缩后的旧 usage 不再代表当前请求，改用当前可见内容估算。</summary>
    /// <param name="projection">当前模型可见投影。</param>
    /// <param name="branch">原始父链，提供用量与编辑的真实先后顺序。</param>
    /// <returns>有效 usage 或可见消息估算。</returns>
    internal static AgentContextUsageEstimate EstimateContextUsage(CodingAgentSessionProjection projection, IReadOnlyList<CodingAgentTreeSessionEntry> branch)
    {
        var estimate = AgentCompaction.EstimateContextTokens(projection.Messages);
        if (estimate.LastUsageIndex is { } messageIndex)
        {
            var index = 0;
            string? sourceId = null;
            foreach (var entry in projection.Entries)
            {
                index += entry.Messages.Count;
                if (messageIndex < index) { sourceId = entry.SourceEntry.GetProperty("id").GetString(); break; }
            }
            var usageIndex = -1;
            var invalidatingIndex = -1;
            for (var position = 0; position < branch.Count; position++)
            {
                if (branch[position].Id == sourceId) usageIndex = position;
                if (branch[position].Type is "compaction" or "context_edit") invalidatingIndex = position;
            }
            if (usageIndex > invalidatingIndex) return estimate;
        }
        var system = Transcript.GetCurrentSystemMessage(projection.Messages);
        var tokens = projection.Messages.Where(message => message is not SystemMessage).Sum(AgentCompaction.EstimateTokens)
            + (system is null ? 0 : AgentCompaction.EstimateTokens(system));
        return new(tokens, 0, tokens, null);
    }

    /// <summary>【CodingAgent】【摘要范围】展开指定窗口中的可见普通消息。</summary>
    /// <param name="entries">投影条目。</param>
    /// <param name="sources">对应原始条目。</param>
    /// <param name="start">起始索引。</param>
    /// <param name="end">不包含的结束索引。</param>
    /// <returns>排除系统声明及压缩摘要后的消息。</returns>
    private static IReadOnlyList<ChatMessage> CollectMessages(IReadOnlyList<CodingAgentProjectedSessionEntry> entries,
        IReadOnlyList<CodingAgentTreeSessionEntry> sources, int start, int end) => Enumerable.Range(start, end - start)
        .Where(index => sources[index].Type != "compaction").SelectMany(index => entries[index].Messages)
        .Where(message => message is not SystemMessage).ToArray();

    /// <summary>【CodingAgent】【保留边界】按尾部预算选择完整消息切点，并保留关联元数据和拆分回合前缀。</summary>
    /// <param name="entries">当前上下文投影。</param>
    /// <param name="sources">对应原始条目。</param>
    /// <param name="start">上次摘要之后的窗口起点。</param>
    /// <param name="keepTokens">保留尾部的估算 token 数。</param>
    /// <returns>保留索引与回合切分信息。</returns>
    private static AgentCutPointResult FindCutPoint(IReadOnlyList<CodingAgentProjectedSessionEntry> entries,
        IReadOnlyList<CodingAgentTreeSessionEntry> sources, int start, int keepTokens)
    {
        var points = Enumerable.Range(start, entries.Count - start).Where(index => sources[index].Type != "compaction"
            && entries[index].Messages.Any(message => message is UserMessage or AssistantMessage or AgentBashExecutionMessage
                or AgentCustomMessage or AgentBranchSummaryMessage or AgentCompactionSummaryMessage)).ToArray();
        if (points.Length == 0) return new(start, -1, false);
        var cut = points[0];
        var tokens = 0;
        var exceeded = false;
        for (var index = entries.Count - 1; index >= start; index--)
        {
            var size = entries[index].Messages.Sum(AgentCompaction.EstimateTokens);
            if (size == 0) continue;
            tokens += size;
            if (tokens < keepTokens) continue;
            exceeded = true;
            cut = points.FirstOrDefault(point => point >= index, points[^1]);
            break;
        }
        // 1. 【CodingAgent】【恢复省略】只有封闭的失败助手及省略编辑后缀可以越过最后一条可见输入
        var suffix = Enumerable.Range(cut + 1, entries.Count - cut - 1).ToArray();
        var omittedIds = suffix.Where(index => IsOmitted(entries[index], sources[index])).Select(index => sources[index].Id).ToHashSet(StringComparer.Ordinal);
        var externalEdit = suffix.Any(index => sources[index].Type == "context_edit" && sources[index].Replacement is { ValueKind: not JsonValueKind.Null }
            && !omittedIds.Contains(sources[index].TargetId!));
        if (exceeded && !externalEdit
            && suffix.Any(index => sources[index].Type == "message" && sources[index].Message?.Role == "assistant" && IsOmitted(entries[index], sources[index]))
            && suffix.All(index => sources[index].Type != "compaction" && (!IsIntrinsicallyVisible(sources[index]) || IsOmitted(entries[index], sources[index])))) cut++;
        while (cut > start && sources[cut - 1].Type != "compaction" && entries[cut - 1].Messages.Count == 0) cut--;
        var turnStart = -1;
        if (cut < entries.Count && !IsTurnStart(entries[cut], sources[cut]))
            for (var index = cut; index >= start; index--)
                if (IsTurnStart(entries[index], sources[index])) { turnStart = index; break; }
        return new(cut, turnStart, turnStart >= 0);
    }

    /// <summary>【CodingAgent】【原始可见性】判断原始条目是否贡献对话消息。</summary>
    /// <param name="source">原始条目。</param>
    /// <returns>未编辑时是否可见。</returns>
    private static bool IsIntrinsicallyVisible(CodingAgentTreeSessionEntry source) => source.Type switch
    {
        "message" => source.Message is not null,
        "custom_message" => true,
        "branch_summary" => !string.IsNullOrWhiteSpace(source.Summary),
        "compaction" => true,
        _ => false
    };

    /// <summary>【CodingAgent】【省略判断】判断原始可见条目是否被编辑完全省略。</summary>
    /// <param name="entry">投影条目。</param>
    /// <param name="source">原始条目。</param>
    /// <returns>是否完全省略。</returns>
    private static bool IsOmitted(CodingAgentProjectedSessionEntry entry, CodingAgentTreeSessionEntry source) =>
        IsIntrinsicallyVisible(source) && entry.Messages.Count == 0;

    /// <summary>【CodingAgent】【回合起点】识别用户或自定义上下文开始的新回合。</summary>
    /// <param name="entry">投影条目。</param>
    /// <param name="source">原始条目。</param>
    /// <returns>是否为新回合起点。</returns>
    private static bool IsTurnStart(CodingAgentProjectedSessionEntry entry, CodingAgentTreeSessionEntry source) =>
        source.Type != "compaction" && entry.Messages.Any(message => message is UserMessage or AgentBashExecutionMessage
            or AgentCustomMessage or AgentBranchSummaryMessage or AgentCompactionSummaryMessage);

    /// <summary>【CodingAgent】【文件记录】复制有效的历史文件路径到去重集合。</summary>
    /// <param name="details">原生摘要详情。</param>
    /// <param name="name">文件列表字段。</param>
    /// <param name="target">目标集合。</param>
    private static void CopyPaths(JsonElement details, string name, ISet<string> target)
    {
        if (!details.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) return;
        foreach (var value in values.EnumerateArray()) if (value.ValueKind == JsonValueKind.String) target.Add(value.GetString()!);
    }
}
