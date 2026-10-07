// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>扩展主动消息及调度选项，区分未指定和明确关闭 triggerTurn。</summary>
/// <param name="Message">用户或自定义消息。</param>
/// <param name="DeliverAs">steer、followUp 或 nextTurn。</param>
/// <param name="TriggerTurn">是否触发回合，空值保留上游默认语义。</param>
/// <param name="ExpandPromptTemplates">用户消息是否展开技能与模板。</param>
/// <param name="FilePath">来源扩展路径。</param>
internal sealed record CodingAgentExtensionMessageDelivery(ChatMessage Message, string? DeliverAs, bool? TriggerTurn,
    bool ExpandPromptTemplates, string FilePath);

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【后台投递】校验会话后接收定时器动作，UI 处理使用独立任务以允许回调扩展。</summary>
    /// <param name="root">后台动作及所属会话。</param>
    private void DispatchBackgroundActions(JsonElement root)
    {
        var bridge = _sessionBridge;
        if (bridge is null || ReadString(root, "sessionId") != bridge.Snapshot().Header.Id) return;
        DispatchMessageActions(root, ReadString(root, "filePath") ?? "");
        var actions = ReadUiActions(root);
        if (actions.Count > 0)
        {
            var copy = root.Clone();
            _ = Task.Run(() =>
            {
                try { DispatchUiActions(copy); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceError("【CodingAgent】【后台交互】{0}", ex.Message); }
            });
        }
    }
    /// <summary>【CodingAgent】【主动消息】把事件及工具中的发送动作按原始顺序交给会话调度器。</summary>
    /// <param name="root">当前调用的完整结果。</param>
    /// <param name="filePath">来源模块。</param>
    private void DispatchMessageActions(JsonElement root, string filePath)
    {
        if (_sessionBridge is null) return;
        foreach (var delivery in ReadMessageActions(root, filePath)) _sessionBridge.EnqueueMessage(delivery);
    }

    /// <summary>【CodingAgent】【完整动作】读取命令、快捷键、工具和事件共享的有序消息操作。</summary>
    /// <param name="root">扩展结果。</param>
    /// <param name="filePath">来源扩展。</param>
    /// <returns>保留多模态和投递三态的消息操作。</returns>
    internal static IReadOnlyList<CodingAgentExtensionMessageDelivery> ReadMessageActions(JsonElement root, string filePath)
    {
        if (!root.TryGetProperty("actions", out var actions)) return [];
        var result = new List<CodingAgentExtensionMessageDelivery>();
        foreach (var action in actions.EnumerateArray())
        {
            ChatMessage? message = ReadString(action, "type") switch
            {
                "sendMessage" => new UserMessage(ReadString(action, "message") ?? ""),
                "userMessage" => new UserMessage(ReadContentBlocks(action.GetProperty("content"), preserveEmpty: true))
                    { Timestamp = ReadUnixMilliseconds(action, "timestamp") ?? DateTimeOffset.UtcNow },
                "customMessage" => new AgentCustomMessage(ReadString(action, "customType") ?? "",
                    ReadContentBlocks(action.GetProperty("content"), preserveEmpty: true), ReadOptionalBool(action, "display") ?? true,
                    ReadOptionalJsonElement(action, "details"), ReadUnixMilliseconds(action, "timestamp") ?? DateTimeOffset.UtcNow),
                _ => null
            };
            if (message is null) continue;
            result.Add(new(message, ReadCustomMessageDeliverAs(action), ReadOptionalBool(action, "triggerTurn"),
                ReadBool(action, "expandPromptTemplates"), filePath));
        }
        return result;
    }
}

internal sealed partial class CodingAgentExtensionSessionBridge
{
    /// <summary>【CodingAgent】【后台保存】把已提交消息同步到绑定存储，后台运行与交互运行共享同一历史。</summary>
    /// <param name="evt">后台模型或消息事件。</param>
    /// <param name="token">当前后台运行的取消信号。</param>
    /// <returns>同步保存完成的任务。</returns>
    private Task PersistBackgroundEventAsync(AgentEvent evt, CancellationToken token)
    {
        if (evt is MessageEndEvent or AgentEndEvent or CodingAgentSessionInfoChangedEvent or CodingAgentThinkingLevelChangedEvent)
        {
            lock (_gate)
            {
                Snapshot();
                _flat?.Save(_runner.Messages, _runner.Model, _runner.SessionName, CodingAgentThinkingLevels.Format(_runner.ThinkingLevel));
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>【CodingAgent】【后台解绑】停止自动运行，按需解除持久化订阅。</summary>
    /// <param name="detach">会话绑定结束时移除持久化回调。</param>
    internal void StopBackgroundDelivery(bool detach = false)
    {
        if (_runner is not RuntimeCodingAgentRunner runner) return;
        if (runner.IsCompacting) runner.Abort();
        _ = runner.StopBackgroundDeliveryAsync();
        if (detach)
        {
            runner.DisableStateNotifications();
            runner.BackgroundEvent -= PersistBackgroundEventAsync;
        }
    }

    /// <summary>【CodingAgent】【运行时释放】扩展进程已停止后，等待在途状态持久化结束。</summary>
    internal void DrainStateNotifications()
    {
        if (_runner is RuntimeCodingAgentRunner runner) runner.StopStateNotificationsAsync().GetAwaiter().GetResult();
    }
    /// <summary>【CodingAgent】【消息接收】只入队，不在 Node 返回结果解析期间重入执行模型。</summary>
    /// <param name="delivery">已脱离 JSON 文档生命周期的消息。</param>
    internal void EnqueueMessage(CodingAgentExtensionMessageDelivery delivery)
    {
        if (_runner is RuntimeCodingAgentRunner runner) runner.EnqueueExtensionMessage(delivery);
        else throw new InvalidOperationException("The runner does not support extension message delivery.");
    }
}
