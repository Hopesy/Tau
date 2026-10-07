// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private Func<string?, CodingAgentSessionStats>? _readSessionStatistics;

    /// <summary>【CodingAgent】【统计绑定】接入完整原始会话统计，避免摘要替换后丢失已计费用量。</summary>
    /// <param name="read">读取最新会话统计的委托。</param>
    internal void ConfigureSessionStatistics(Func<string?, CodingAgentSessionStats> read) => _readSessionStatistics = read;

    /// <summary>【CodingAgent】【上下文查询】返回当前窗口使用情况，没有有效模型窗口时为空。</summary>
    /// <returns>当前有效用量或压缩后的未知状态。</returns>
    public CodingAgentContextUsage? GetContextUsage() => GetSessionStats().ContextUsage;
}

internal sealed partial class CodingAgentExtensionSessionBridge
{
    /// <summary>【CodingAgent】【会话统计】对全部原始条目汇总计费，对当前分支投影计算上下文占用。</summary>
    /// <param name="sessionFile">调用方提供的后备会话路径。</param>
    /// <returns>包含历史总量和当前上下文的统计。</returns>
    private CodingAgentSessionStats ReadSessionStatistics(string? sessionFile)
    {
        lock (_gate)
        {
            var snapshot = Snapshot();
            var branch = GetSnapshotBranch(snapshot);
            var projection = CodingAgentTreeSessionStore.ProjectBranch(branch);
            var messages = snapshot.Entries.Where(entry => entry.Type == "message" && entry.Message is not null)
                .Select(entry => CodingAgentSessionStore.ToMessage(entry.Message!)).OfType<ChatMessage>().ToArray();
            var estimate = CodingAgentProjectedCompaction.EstimateContextUsage(projection, branch);
            var compactionIndex = -1;
            for (var index = 0; index < branch.Count; index++) if (branch[index].Type == "compaction") compactionIndex = index;
            var visibleUsage = projection.Entries.Where(entry => entry.Messages.Any(message => message is AssistantMessage
                { StopReason: not (StopReason.Aborted or StopReason.Error), Usage: { } usage } && AgentCompaction.CalculateContextTokens(usage) > 0))
                .Select(entry => entry.SourceEntry.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
            var unknown = compactionIndex >= 0 && !branch.Skip(compactionIndex + 1).Any(entry => visibleUsage.Contains(entry.Id));
            var limits = (_runner as RuntimeCodingAgentRunner)?.GetEffectiveContextModel() ?? _runner.Model;
            var window = limits.ContextWindow ?? 0;
            var context = window <= 0 ? null : new CodingAgentContextUsage(unknown ? null : estimate.Tokens, window,
                unknown ? null : 100.0 * estimate.Tokens / window);
            return new CodingAgentSessionStats(_runner.Model.Provider, _runner.Model.Id, messages.Length,
                messages.OfType<UserMessage>().Count(), messages.OfType<AssistantMessage>().Count(), messages.OfType<ToolResultMessage>().Count(),
                messages.OfType<AssistantMessage>().Sum(message => message.Content.OfType<ToolCallContent>().Count()), estimate.Tokens,
                limits.ContextWindow, _runner.SessionName, snapshot.SessionFile ?? sessionFile)
            {
                SessionId = snapshot.Header.Id, ContextUsage = context,
                CacheWaste = (_runner as RuntimeCodingAgentRunner)?.CalculateCacheWaste(snapshot.Entries) ?? new(0, 0, 0),
                UsageBreakdown = CodingAgentUsageAccounting.GetUsageCostBreakdown(snapshot.Entries)
            }.WithUsage(CodingAgentSessionUsageSummary.FromEntries(snapshot.Entries));
        }
    }
}

public sealed partial class CodingAgentTreeSessionStore
{
    /// <summary>【CodingAgent】【独立用量】追加不参与模型上下文的已计费用量，例如缓存预热。</summary>
    /// <param name="kind">用量类别。</param>
    /// <param name="provider">模型提供方。</param>
    /// <param name="model">计费模型标识。</param>
    /// <param name="usage">标准模型用量。</param>
    /// <param name="note">可选说明。</param>
    /// <returns>新用量条目标识。</returns>
    public string AppendUsage(string kind, string provider, string model, Usage usage, string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        lock (SyncRoot)
        {
            var state = ReadState();
            var id = CreateEntryId(state.EntryIds);
            var message = JsonSerializer.SerializeToElement(CodingAgentSessionStore.FromMessage(new AssistantMessage([]) { Usage = usage }),
                CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage);
            AppendEntry(new()
            {
                Type = "usage", Id = id, ParentId = state.LeafId, Timestamp = DateTimeOffset.UtcNow,
                Kind = kind, Provider = provider, Model = model, Usage = message.GetProperty("usage").Clone(), Note = note
            });
            return id;
        }
    }

    /// <summary>【CodingAgent】【全部用量】统计全部原始分支、摘要与独立用量，保留会话实际总费用。</summary>
    /// <returns>会话计费总量。</returns>
    public CodingAgentSessionUsageSummary GetSessionUsageSummary() => CodingAgentSessionUsageSummary.FromEntries(ReadState().Entries);
}
