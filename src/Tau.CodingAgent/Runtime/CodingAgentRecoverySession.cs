// 作者：xxx
using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

internal sealed partial class CodingAgentExtensionSessionBridge
{
    /// <summary>【CodingAgent】【持久恢复】把失败尝试保存为上下文省略记录，保留原始消息、费用和会话身份。</summary>
    /// <param name="messages">需要从模型上下文移除的消息。</param>
    /// <returns>已保存的编辑条目。</returns>
    private IReadOnlyList<CodingAgentTreeSessionEntry> OmitRecoveryMessages(IReadOnlyList<ChatMessage> messages)
    {
        lock (_gate)
        {
            var snapshot = Snapshot();
            var branch = GetSnapshotBranch(snapshot);
            var targets = new List<string>();
            // 1. 【CodingAgent】【来源校验】先找齐全部目标，避免缺失来源时提交半组编辑
            foreach (var message in messages)
            {
                var expected = JsonSerializer.SerializeToElement(CodingAgentSessionStore.FromMessage(message), CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage);
                var target = branch.LastOrDefault(entry => entry.Type == "message" && entry.Message is not null &&
                    JsonElement.DeepEquals(expected, JsonSerializer.SerializeToElement(entry.Message, CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage)));
                if (target is not null) targets.Add(target.Id);
                else if (_runner.Messages.Any(current => ReferenceEquals(current, message)))
                    throw new InvalidOperationException("Cannot persist recovery omission because a projected message has no source entry");
            }
            var saved = new List<CodingAgentTreeSessionEntry>();
            foreach (var target in targets.Distinct(StringComparer.Ordinal))
            {
                if (_tree is not null)
                {
                    var id = _tree.Store.AppendContextEdit(target, null);
                    saved.Add(_tree.Store.ReadExtensionSnapshot().Entries.First(entry => entry.Id == id));
                }
                else
                {
                    var entry = new CodingAgentTreeSessionEntry
                    {
                        Type = "context_edit", Id = Guid.NewGuid().ToString("N"), ParentId = _memoryLeafId,
                        Timestamp = DateTimeOffset.UtcNow, TargetId = target,
                        Replacement = JsonSerializer.SerializeToElement((string?)null, CodingAgentSessionJsonContext.Default.String)
                    };
                    AppendMemoryEntry(entry);
                    saved.Add(entry);
                }
            }
            // 2. 【CodingAgent】【投影恢复】刷新保存游标并保留等待队列，省略消息不应在下次保存时再次出现
            CodingAgentSessionSnapshot restored;
            if (_tree is not null) restored = _tree.LoadSnapshot().ToFlatSnapshot();
            else
            {
                var projection = CodingAgentTreeSessionStore.ProjectBranch(ReadReplacementBranch(_memoryEntries, _memoryLeafId), legacyMessages: true);
                restored = new(projection.Messages, _runner.Model.Provider, _runner.Model.Id, _runner.SessionName)
                    { ThinkingLevel = CodingAgentThinkingLevels.Format(_runner.ThinkingLevel) };
            }
            if (_runner is RuntimeCodingAgentRunner runtime) runtime.RestoreCompactedSession(restored);
            else _runner.RestoreSession(restored);
            _memoryMessageCount = _runner.Messages.Count;
            _memoryFirstMessage = _runner.Messages.FirstOrDefault();
            _flat?.Save(_runner.Messages, _runner.Model, _runner.SessionName, CodingAgentThinkingLevels.Format(_runner.ThinkingLevel));
            return saved;
        }
    }
}
