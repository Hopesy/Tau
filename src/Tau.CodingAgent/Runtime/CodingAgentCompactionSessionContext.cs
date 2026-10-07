// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【压缩来源】携带当前分支的稳定条目标识、上游准备数据和原始事件条目。</summary>
/// <param name="Branch">当前分支原始条目。</param>
/// <param name="Preparation">包含摘要输入和尾部边界的准备结果。</param>
internal sealed record CodingAgentCompactionSessionContext(IReadOnlyList<CodingAgentTreeSessionEntry> Branch,
    AgentCompactionPreparation? Preparation);

internal sealed partial class CodingAgentExtensionSessionBridge
{
    /// <summary>【CodingAgent】【压缩准备】按实际分支、上下文编辑和上次摘要建立上游准备数据。</summary>
    /// <returns>稳定会话条目及摘要准备结果。</returns>
    private CodingAgentCompactionSessionContext ReadCompactionContext()
    {
        lock (_gate)
        {
            var snapshot = Snapshot();
            var branch = GetSnapshotBranch(snapshot);
            var settings = _runner is RuntimeCodingAgentRunner { HasCompactionConfiguration: true } runtime
                ? runtime.CompactionSettings : _tree?.CompactionSettings ?? AgentCompactionSettings.Default;
            return new(branch, CodingAgentProjectedCompaction.Prepare(branch, settings));
        }
    }

    /// <summary>【CodingAgent】【压缩提交】统一保存摘要并刷新上下文，保留会话身份和已生成的保留边界。</summary>
    /// <param name="result">已完成生成或由扩展提供的摘要。</param>
    /// <returns>补充持久化标识与压缩后估算的结果。</returns>
    private CodingAgentCompactionResult CommitPreparedCompaction(CodingAgentCompactionResult result)
    {
        lock (_gate)
        {
            string id;
            if (_tree is not null) id = _tree.RecordCompaction(_runner, result);
            else
            {
                var system = Transcript.GetCurrentSystemMessage(_runner.Messages);
                id = Guid.NewGuid().ToString("N");
                AppendMemoryEntry(new()
                {
                    Type = "compaction", Id = id, ParentId = _memoryLeafId, Timestamp = DateTimeOffset.UtcNow,
                    Summary = result.Summary, FirstKeptEntryId = result.FirstKeptEntryId, TokensBefore = result.TokensBefore,
                    Details = result.Details, Usage = result.Usage, FromHook = result.FromHook ? true : null,
                    SystemMessage = system is null ? null : CodingAgentSessionStore.FromMessage(system)
                });
                var branch = GetSnapshotBranch(new() { Header = _memoryHeader, Entries = _memoryEntries, LeafId = id });
                var projection = CodingAgentTreeSessionStore.ProjectBranch(branch, legacyMessages: true);
                var compacted = new CodingAgentSessionSnapshot(projection.Messages, _runner.Model.Provider, _runner.Model.Id, _runner.SessionName)
                    { ThinkingLevel = CodingAgentThinkingLevels.Format(_runner.ThinkingLevel) };
                if (_runner is RuntimeCodingAgentRunner runtime) runtime.RestoreCompactedSession(compacted);
                else _runner.RestoreSession(compacted);
                _memoryMessageCount = _runner.Messages.Count;
                _memoryFirstMessage = _runner.Messages.FirstOrDefault();
            }
            _flat?.Save(_runner.Messages, _runner.Model, _runner.SessionName, CodingAgentThinkingLevels.Format(_runner.ThinkingLevel));
            return result with { StoredEntryId = id, MessagesAfter = _runner.Messages.Count, EstimatedTokensAfter = CodingAgentTokenEstimator.Estimate(_runner.Messages) };
        }
    }

    /// <summary>【CodingAgent】【分支读取】沿当前叶节点取完整父链，禁止把其他分支纳入摘要。</summary>
    /// <param name="snapshot">已同步的会话快照。</param>
    /// <returns>从根到当前叶节点的条目。</returns>
    private static IReadOnlyList<CodingAgentTreeSessionEntry> GetSnapshotBranch(CodingAgentExtensionSessionSnapshot snapshot)
    {
        var byId = snapshot.Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var branch = new List<CodingAgentTreeSessionEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = snapshot.LeafId; current is not null && seen.Add(current) && byId.TryGetValue(current, out var entry); current = entry.ParentId)
            branch.Add(entry);
        branch.Reverse();
        return branch;
    }
}
