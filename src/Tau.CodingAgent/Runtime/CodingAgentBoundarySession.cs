// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

internal sealed partial class CodingAgentExtensionSessionBridge
{
    private CodingAgentExtensionSessionSnapshot? _boundaryOrigin;

    /// <summary>【CodingAgent】【边界开始】同步已完成消息并登记草稿所属会话及分支</summary>
    /// <returns>边界开始时的持久或内存快照</returns>
    internal CodingAgentExtensionSessionSnapshot BeginBoundary()
    {
        lock (_gate)
        {
            if (_boundaryOrigin is not null) throw new InvalidOperationException("A session boundary is already active.");
            return _boundaryOrigin = Snapshot();
        }
    }

    /// <summary>【CodingAgent】【边界结束】释放当前草稿的身份约束</summary>
    internal void EndBoundary() { lock (_gate) _boundaryOrigin = null; }

    /// <summary>【CodingAgent】【边界身份】拒绝把旧会话或已离开分支的草稿应用到新上下文</summary>
    /// <returns>通过身份校验的最新快照；同一分支中新追加的消息可以参与预览</returns>
    private CodingAgentExtensionSessionSnapshot ReadBoundarySnapshot()
    {
        var origin = _boundaryOrigin ?? throw new InvalidOperationException("No session boundary is active.");
        var snapshot = Snapshot();
        if (snapshot.Header.Id != origin.Header.Id || origin.LeafId is { } leaf && !GetSnapshotBranch(snapshot).Any(entry => entry.Id == leaf))
            throw new InvalidOperationException("Session or branch changed during the boundary.");
        return snapshot;
    }

    /// <summary>【CodingAgent】【边界预览】在临时分支上验证草稿并生成来源、模型消息和待处理消息</summary>
    /// <param name="drafts">按顺序应用但尚未保存的草稿数组</param>
    /// <param name="boundary">turn_end 或 agent_before_settle</param>
    /// <returns>可供下一处理器查看的完整上下文及可继续标志</returns>
    internal JsonElement PreviewBoundary(JsonElement drafts, string boundary)
    {
        lock (_gate)
        {
            var snapshot = ReadBoundarySnapshot();
            var branch = GetSnapshotBranch(snapshot).ToList();
            BuildBoundaryEntries(branch, drafts);
            var projection = CodingAgentTreeSessionStore.ProjectBranch(branch);
            var llm = AgentHarnessMessages.ConvertToLlm(projection.Messages);
            var runtime = _runner as RuntimeCodingAgentRunner;
            var pending = runtime?.PeekBoundaryMessages() ?? [];
            var hasQueues = runtime?.HasBoundaryQueuedMessages == true;
            var customPending = runtime?.HasBoundaryCustomMessages == true;
            var lastIsAssistant = llm.LastOrDefault() is AssistantMessage;
            return JsonSerializer.SerializeToElement(new
            {
                contextEntries = projection.Entries.Select(entry => new { sourceEntry = entry.SourceEntry, messages = entry.Messages.Select(SerializeBoundaryMessage) }),
                contextMessages = projection.Messages.Select(SerializeBoundaryMessage),
                llmMessages = llm.Select(SerializeBoundaryMessage),
                pendingMessages = pending.Select(SerializeBoundaryMessage),
                canContinue = llm.Any(message => message is not SystemMessage) && !lastIsAssistant || customPending ||
                    (boundary == "turn_end" ? hasQueues : lastIsAssistant && hasQueues)
            });
        }
    }

    /// <summary>【CodingAgent】【边界提交】先验证全部草稿，再追加并刷新投影，避免无效末条留下部分写入</summary>
    /// <param name="drafts">最后一个处理器确认的有序草稿</param>
    /// <returns>实际提交的条目；预览生成的临时标识不会写入历史</returns>
    internal IReadOnlyList<CodingAgentTreeSessionEntry> CommitBoundary(JsonElement drafts)
    {
        lock (_gate)
        {
            // 1. 【CodingAgent】【草稿验证】在持久存储锁内重建最终分支，先完成整组语义校验
            lock (_tree?.Store.SyncRoot ?? _gate)
            {
                var snapshot = ReadBoundarySnapshot();
                var branch = GetSnapshotBranch(snapshot).ToList();
                var appended = BuildBoundaryEntries(branch, drafts);
                if (appended.Count == 0) return appended;
                if (_flat is not null && _tree is null)
                    throw new InvalidOperationException("Persistent boundary entries require a JSONL session.");
                foreach (var entry in appended)
                {
                    if (_tree is not null) _tree.Store.AppendExtensionEntry(entry);
                    else AppendMemoryEntry(entry);
                }
                // 2. 【CodingAgent】【草稿投影】推进持久化游标并保留运行器当前模型选择及等待队列
                _tree?.LoadSnapshot();
                var projection = CodingAgentTreeSessionStore.ProjectBranch(branch, legacyMessages: true);
                if (_runner is RuntimeCodingAgentRunner runtime) runtime.ReplaceBoundaryContext(projection.Messages);
                else _runner.RestoreSession(new(projection.Messages, _runner.Model.Provider, _runner.Model.Id, _runner.SessionName)
                    { ThinkingLevel = CodingAgentThinkingLevels.Format(_runner.ThinkingLevel) });
                _memoryMessageCount = _runner.Messages.Count;
                _memoryFirstMessage = _runner.Messages.FirstOrDefault();
                _flat?.Save(_runner.Messages, _runner.Model, _runner.SessionName, CodingAgentThinkingLevels.Format(_runner.ThinkingLevel));
                return appended;
            }
        }
    }

    /// <summary>【CodingAgent】【草稿构建】逐条验证四种边界操作，并在临时分支中生成独立条目</summary>
    /// <param name="branch">追加后供预览或提交使用的分支副本</param>
    /// <param name="drafts">扩展返回的草稿 JSON</param>
    /// <returns>此次构建的新条目</returns>
    private static IReadOnlyList<CodingAgentTreeSessionEntry> BuildBoundaryEntries(List<CodingAgentTreeSessionEntry> branch, JsonElement drafts)
    {
        if (drafts.ValueKind != JsonValueKind.Array) throw new ArgumentException("Boundary entries must be an array.");
        var appended = new List<CodingAgentTreeSessionEntry>();
        foreach (var draft in drafts.EnumerateArray())
        {
            if (draft.ValueKind != JsonValueKind.Object) throw new ArgumentException("Boundary draft must be an object.");
            var type = draft.GetProperty("type").GetString();
            var value = new JsonObject
            {
                ["type"] = type, ["id"] = Guid.NewGuid().ToString("N"),
                ["parentId"] = branch.LastOrDefault()?.Id, ["timestamp"] = DateTimeOffset.UtcNow
            };
            switch (type)
            {
                case "custom":
                case "custom_message":
                    var customType = draft.GetProperty("customType").GetString();
                    if (string.IsNullOrWhiteSpace(customType)) throw new ArgumentException("Custom session entry type is required.");
                    value["customType"] = customType;
                    if (type == "custom") CopyBoundaryProperty(draft, value, "data");
                    else
                    {
                        var content = draft.GetProperty("content");
                        if (content.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
                            throw new ArgumentException("Custom message content must be a string or array.");
                        CopyBoundaryProperty(draft, value, "content");
                        value["display"] = draft.GetProperty("display").GetBoolean();
                        CopyBoundaryProperty(draft, value, "details");
                    }
                    break;
                case "context_edit":
                    var targetId = draft.GetProperty("targetId").GetString();
                    var target = branch.FirstOrDefault(entry => entry.Id == targetId)
                        ?? throw new ArgumentException("Context edit target is not on the active branch.");
                    if (target.Type != "custom_message" && (target.Type != "message" || target.Message?.Role is not ("user" or "assistant" or "toolResult" or "custom")))
                        throw new ArgumentException("Context edit target does not contribute editable model content.");
                    value["targetId"] = targetId;
                    value["replacement"] = JsonNode.Parse(CodingAgentTreeSessionStore.NormalizeContextReplacement(
                        draft.GetProperty("replacement"), target.Message?.Role).GetRawText());
                    break;
                case "compaction":
                    var summary = draft.GetProperty("summary").GetString();
                    if (string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("Compaction summary cannot be empty.");
                    value["summary"] = summary.Trim();
                    value["firstKeptEntryId"] = draft.GetProperty("firstKeptEntryId").GetString();
                    var projection = CodingAgentTreeSessionStore.ProjectBranch(branch);
                    value["tokensBefore"] = CodingAgentProjectedCompaction.EstimateContextUsage(projection, branch).Tokens;
                    value["fromHook"] = true;
                    CopyBoundaryProperty(draft, value, "details");
                    CopyBoundaryProperty(draft, value, "usage");
                    if (Transcript.GetCurrentSystemMessage(projection.Messages) is { } system)
                        value["systemMessage"] = JsonNode.Parse(SerializeBoundaryMessage(system).GetRawText());
                    break;
                default: throw new ArgumentException("Unsupported boundary draft type: " + type);
            }
            var entry = JsonSerializer.Deserialize(value.ToJsonString(), CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry)!;
            branch.Add(entry);
            appended.Add(entry);
        }
        return appended;
    }

    /// <summary>【CodingAgent】【草稿字段】复制可选 JSON 字段，保留显式空值</summary>
    /// <param name="source">输入草稿</param><param name="target">标准条目</param><param name="name">属性名称</param>
    private static void CopyBoundaryProperty(JsonElement source, JsonObject target, string name)
    {
        if (source.TryGetProperty(name, out var value)) target[name] = JsonNode.Parse(value.GetRawText());
    }

    /// <summary>【CodingAgent】【边界消息】使用会话原生消息协议，保留摘要和工具元数据</summary>
    /// <param name="message">上下文或等待消息</param><returns>独立 JSON 消息</returns>
    internal static JsonElement SerializeBoundaryMessage(ChatMessage message) => JsonSerializer.SerializeToElement(
        CodingAgentSessionStore.FromMessage(message), CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage);

    /// <summary>【CodingAgent】【消息来源】从当前分支查找最终消息对应的真实条目标识</summary>
    /// <param name="snapshot">已经同步最终消息的快照</param><param name="message">完成后的助手或工具结果</param>
    /// <returns>匹配条目标识；没有对应来源时为空</returns>
    internal static string? FindBoundaryMessageEntry(CodingAgentExtensionSessionSnapshot snapshot, ChatMessage message)
    {
        var expected = SerializeBoundaryMessage(message);
        return GetSnapshotBranch(snapshot).LastOrDefault(entry => entry.Type == "message" && entry.Message is not null &&
            JsonElement.DeepEquals(expected, JsonSerializer.SerializeToElement(entry.Message, CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage)))?.Id;
    }
}
