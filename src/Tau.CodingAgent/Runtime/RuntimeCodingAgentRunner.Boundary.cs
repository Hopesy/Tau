// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Observability;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private CodingAgentExtensionSessionBridge? _boundarySession;
    private readonly HashSet<ChatMessage> _boundaryDispatchedMessages = new(ReferenceEqualityComparer.Instance);
    private int _boundaryTurnIndex;
    private string _lastBoundaryOutcome = "completed";
    internal bool HasBoundaryQueuedMessages => _runtime.HasQueuedMessages;
    internal bool HasBoundaryCustomMessages => _deferredExtensionMessages.Count > 0;

    /// <summary>【CodingAgent】【边界绑定】关联实际会话，用于预览及提交扩展草稿</summary>
    /// <param name="session">当前会话桥接器</param>
    internal void ConfigureBoundarySession(CodingAgentExtensionSessionBridge session)
    {
        _boundarySession = session;
        ConfigureCacheWarmingSession(session);
    }

    /// <summary>【CodingAgent】【边界消息】预览当前调度将选中的消息及尚未追加的自定义消息</summary>
    /// <returns>不会消费任何队列的独立消息列表</returns>
    internal IReadOnlyList<ChatMessage> PeekBoundaryMessages() =>
        [.. _runtime.PeekQueuedMessages(), .. _deferredExtensionMessages];

    /// <summary>【CodingAgent】【边界投递】在处理器间接收主动消息，使后续预览反映最新队列</summary>
    /// <param name="token">调度取消信号</param>
    internal void PrepareBoundaryPreview(CancellationToken token) => _extensionRunMessages.AddRange(DrainExtensionDeliveries(true, token));

    /// <summary>【CodingAgent】【边界上下文】替换投影消息，保留模型选择、工具详情和等待队列</summary>
    /// <param name="messages">草稿提交后的上下文</param>
    internal void ReplaceBoundaryContext(IReadOnlyList<ChatMessage> messages)
    {
        CancelCacheWarming();
        _runtime.ReplaceMessages(messages);
        Interlocked.Increment(ref _historyRevision);
    }

    /// <summary>【CodingAgent】【回合边界】调用扩展并提交草稿，随后执行原有完成钩子，显式 End 优先</summary>
    /// <param name="previous">调用方原先配置的完成钩子</param><param name="turn">最终助手和工具结果</param>
    /// <param name="logContext">日志上下文</param><param name="token">Agent 取消信号</param>
    /// <returns>合并后的继续或结束决策</returns>
    private async Task<AgentTurnDecision?> FinishBoundaryTurnAsync(
        Func<AgentLoopTurnContext, CancellationToken, Task<AgentTurnDecision?>>? previous,
        AgentLoopTurnContext turn, TauRuntimeLogContext logContext, CancellationToken token)
    {
        _boundaryDispatchedMessages.Add(turn.Message);
        var shouldContinue = await DispatchTurnBoundaryAsync(turn.Message, turn.ToolResults, logContext).ConfigureAwait(false);
        var previousDecision = previous is null ? null : await previous(turn, token).ConfigureAwait(false);
        return previousDecision == AgentTurnDecision.End ? previousDecision
            : shouldContinue || previousDecision == AgentTurnDecision.Continue ? AgentTurnDecision.Continue : null;
    }

    /// <summary>【CodingAgent】【回合草稿】为扩展提供最终消息的真实条目标识，并验证其继续请求</summary>
    /// <param name="message">最终助手消息</param><param name="toolResults">本回合最终工具结果</param>
    /// <param name="logContext">日志上下文</param><returns>是否显式要求继续</returns>
    private async Task<bool> DispatchTurnBoundaryAsync(AssistantMessage message, IReadOnlyList<ToolResultMessage> toolResults, TauRuntimeLogContext logContext)
    {
        _lastBoundaryOutcome = message.StopReason switch { StopReason.Aborted => "aborted", StopReason.Error => "error", _ => "completed" };
        if (_boundarySession is null || _extensionLifecycleEventSink?.HasBoundaryHandlers("turn_end") != true) return false;
        try
        {
            var snapshot = _boundarySession.BeginBoundary();
            var messageId = CodingAgentExtensionSessionBridge.FindBoundaryMessageEntry(snapshot, message)
                ?? throw new InvalidOperationException("turn_end could not resolve the persisted assistant entry ID");
            var initial = new JsonObject
            {
                ["type"] = "turn_end", ["turnIndex"] = _boundaryTurnIndex,
                ["message"] = JsonNode.Parse(CodingAgentExtensionSessionBridge.SerializeBoundaryMessage(message).GetRawText()),
                ["toolResults"] = new JsonArray(toolResults.Select(result => JsonNode.Parse(CodingAgentExtensionSessionBridge.SerializeBoundaryMessage(result).GetRawText())).ToArray())
            };
            initial["messageEntryId"] = messageId;
            initial["toolResultEntryIds"] = new JsonArray(toolResults.Select(result => CodingAgentExtensionSessionBridge.FindBoundaryMessageEntry(snapshot, result))
                .Where(id => id is not null).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            initial["outcome"] = _lastBoundaryOutcome;
            return await DispatchBoundaryDraftsAsync(initial, logContext).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogExtensionEventError(new("<boundary>", "extension", "javascript", "turn_end", exception.Message), logContext);
            return false;
        }
        finally { _boundarySession.EndBoundary(); }
    }

    /// <summary>【CodingAgent】【草稿流水线】初始化预览，串行执行处理器，校验并提交最终草稿</summary>
    /// <param name="initial">边界类型及消息来源等固定字段</param><param name="logContext">日志上下文</param>
    /// <returns>最终上下文允许执行时的继续标志</returns>
    private async Task<bool> DispatchBoundaryDraftsAsync(JsonObject initial, TauRuntimeLogContext logContext)
    {
        var type = initial["type"]!.GetValue<string>();
        var empty = JsonSerializer.SerializeToElement(Array.Empty<object>());
        initial["entries"] = new JsonArray();
        initial["continue"] = false;
        initial["context"] = JsonNode.Parse(_boundarySession!.PreviewBoundary(empty, type).GetRawText());
        initial["valid"] = true;
        var result = await _extensionLifecycleEventSink!.EmitBoundaryEventAsync(JsonSerializer.SerializeToElement(initial),
            error => LogExtensionEventError(error, logContext)).ConfigureAwait(false);
        if (result is not { } final) return false;
        var valid = final.GetProperty("valid").ValueKind == JsonValueKind.True;
        PrepareBoundaryPreview(CancellationToken.None);
        var appended = _boundarySession.CommitBoundary(valid ? final.GetProperty("entries") : empty);
        foreach (var entry in appended)
        {
            var notification = new CodingAgentEntryAppendedEvent(JsonSerializer.SerializeToElement(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry));
            if (_nestedToolEvents is { } writer) writer.TryWrite(notification);
            else _extensionMessageEvents.Enqueue(notification);
        }
        if (type == "agent_before_settle") FlushBoundaryCustomMessages();
        var requested = valid && final.GetProperty("continue").ValueKind == JsonValueKind.True;
        if (!requested && !(type == "agent_before_settle" && _runtime.HasQueuedMessages)) return false;
        if (_boundarySession.PreviewBoundary(empty, type).GetProperty("canContinue").GetBoolean()) return true;
        if (requested) LogExtensionEventError(new("<boundary>", "extension", "javascript", type, type + " requested continuation without runnable model context"), logContext);
        return false;
    }
}
