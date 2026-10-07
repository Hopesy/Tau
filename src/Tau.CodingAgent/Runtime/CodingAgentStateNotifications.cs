// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【会话名称】供宿主刷新会话名称的事件。</summary>
public sealed record CodingAgentSessionInfoChangedEvent(string? Name) : AgentEvent("session_info_changed");
/// <summary>【CodingAgent】【思考等级】供宿主刷新实际思考等级的事件。</summary>
public sealed record CodingAgentThinkingLevelChangedEvent(string Level) : AgentEvent("thinking_level_changed");

public sealed partial class RuntimeCodingAgentRunner
{
    private readonly object _stateNotificationGate = new();
    private Task _stateNotifications = Task.CompletedTask;
    private volatile bool _stateNotificationsEnabled;
    private long _stateBindingGeneration;

    /// <summary>【CodingAgent】【状态事件】会话完成绑定后启用事件，初始属性赋值不触发未绑定的扩展。</summary>
    internal void EnableStateNotifications()
    {
        SetQueueNotificationsEnabled(true);
        lock (_stateNotificationGate)
        {
            Interlocked.Increment(ref _stateBindingGeneration);
            _stateNotificationsEnabled = true;
        }
    }

    /// <summary>【CodingAgent】【状态释放】解绑后不再向旧会话投递排队通知。</summary>
    internal void DisableStateNotifications()
    {
        SetQueueNotificationsEnabled(false);
        CancelCacheWarming();
        lock (_stateNotificationGate)
        {
            _stateNotificationsEnabled = false;
            Interlocked.Increment(ref _stateBindingGeneration);
        }
    }

    /// <summary>【CodingAgent】【会话名称】修改用户可见名称并发布变更通知。</summary>
    /// <param name="name">新名称，空值或空白表示清除。</param>
    public void SetSessionName(string? name)
    {
        SessionName = string.IsNullOrWhiteSpace(name) ? null : name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        NotifySessionInfoChanged();
    }

    /// <summary>【CodingAgent】【会话名称】已由会话管理器持久化名称时，仅补发通知。</summary>
    internal void NotifySessionInfoChanged() => QueueStateNotification(
        SessionName is null ? JsonSerializer.SerializeToElement(new { type = "session_info_changed" })
            : JsonSerializer.SerializeToElement(new { type = "session_info_changed", name = SessionName }), new CodingAgentSessionInfoChangedEvent(SessionName));

    /// <summary>【CodingAgent】【MCP 变更】离开同步操作通道后向扩展发布独立服务器快照。</summary>
    /// <param name="servers">已提交的原生服务器数组。</param>
    internal void NotifyMcpServersChanged(JsonElement servers) => QueueStateNotification(
        JsonSerializer.SerializeToElement(new { type = "mcp_servers_change", servers }));

    /// <summary>【CodingAgent】【状态事件】离开同步协议操作后串行分发通知，避免持有输出读取锁等待 Node。</summary>
    /// <param name="payload">扩展事件对象。</param><param name="hostEvent">可选宿主通知。</param>
    private void QueueStateNotification(JsonElement payload, AgentEvent? hostEvent = null)
    {
        if (!_stateNotificationsEnabled) return;
        var sink = _extensionLifecycleEventSink;
        var generation = sink?.CompactionGeneration;
        var session = SessionId;
        var bindingGeneration = Interlocked.Read(ref _stateBindingGeneration);
        lock (_stateNotificationGate)
        {
            if (!_stateNotificationsEnabled) return;
            var previous = _stateNotifications;
            // 1. 【CodingAgent】【通知完成】调用方关闭会话时可能同步等待后继通知，禁止在前驱完成栈内执行外部延续
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stateNotifications = completion.Task;
            _ = Task.Run(async () =>
            {
                try
                {
                    try { await previous.ConfigureAwait(false); } catch (Exception error) { LogRunError(error, CreateRunLogContext()); }
                    if (!_stateNotificationsEnabled || bindingGeneration != Interlocked.Read(ref _stateBindingGeneration) || session != SessionId || generation != sink?.CompactionGeneration) return;
                    if (hostEvent is not null) await PublishBackgroundEventAsync(hostEvent, CancellationToken.None).ConfigureAwait(false);
                    if (!_stateNotificationsEnabled || bindingGeneration != Interlocked.Read(ref _stateBindingGeneration) || session != SessionId || generation != sink?.CompactionGeneration) return;
                    if (sink is not null) await sink.EmitNotificationAsync(payload, error => LogExtensionEventError(error, CreateRunLogContext()), CancellationToken.None, generation!.Value).ConfigureAwait(false);
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { completion.TrySetResult(); }
            });
        }
    }

    /// <summary>【CodingAgent】【状态排空】等待已经提交的状态通知完成。</summary>
    /// <returns>状态通知完成任务。</returns>
    internal Task WaitForStateNotificationsAsync() { lock (_stateNotificationGate) return _stateNotifications; }

    /// <summary>【CodingAgent】【通知关闭】停止接受新通知，并等待已经进入宿主持久化的通知结束后再释放会话。</summary>
    /// <returns>全部通知退出的任务。</returns>
    internal async Task StopStateNotificationsAsync()
    {
        DisableStateNotifications();
        if (_cacheWarmer is { } warmer) await warmer.DisposeAsync().ConfigureAwait(false);
        await WaitForQueueNotificationsAsync().ConfigureAwait(false);
        try { await WaitForStateNotificationsAsync().ConfigureAwait(false); }
        catch (Exception error) { LogRunError(error, CreateRunLogContext()); }
    }

    /// <summary>【CodingAgent】【模型切换】更新模型和有效思考等级，等待扩展收到带来源的模型变更事件。</summary>
    /// <param name="providerId">提供方名称。</param><param name="modelId">模型名称。</param>
    /// <param name="source">set、cycle 或 restore。</param><param name="token">取消信号。</param>
    /// <param name="thinkingLevelOverride">作用域显式指定的思考等级，空值保持当前等级。</param>
    /// <returns>切换后的模型。</returns>
    internal async Task<Model> SelectModelWithSourceAsync(string? providerId, string? modelId, string source, CancellationToken token = default, string? thinkingLevelOverride = null)
    {
        var previous = Model;
        var model = SelectModelCore(providerId, modelId);
        ThinkingLevel = CodingAgentThinkingLevels.ClampForModel(model,
            thinkingLevelOverride is null ? ThinkingLevel : CodingAgentThinkingLevels.ParseOrNull(thinkingLevelOverride));
        await PublishModelSelectionAsync(previous, source, token).ConfigureAwait(false);
        return model;
    }

    /// <summary>【CodingAgent】【模型通知】相同模型不重复通知，恢复后可向新扩展实例补发事件。</summary>
    /// <param name="previous">切换前模型。</param><param name="source">切换来源。</param><param name="token">取消信号。</param>
    /// <returns>所有模型选择处理器完成的任务。</returns>
    internal async Task PublishModelSelectionAsync(Model previous, string source, CancellationToken token)
    {
        if (!_stateNotificationsEnabled || previous.Provider == Model.Provider && previous.Id == Model.Id || _extensionLifecycleEventSink is not { } sink) return;
        await sink.EmitNotificationAsync(CreateModelSelectionEvent(previous, source), error => LogExtensionEventError(error, CreateRunLogContext()), token, sink.CompactionGeneration).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【模型协议】构建带原生模型字段的选择通知。</summary>
    /// <param name="previous">切换前模型。</param><param name="source">切换来源。</param><returns>模型事件。</returns>
    private JsonElement CreateModelSelectionEvent(Model previous, string source) => JsonSerializer.SerializeToElement(new
    {
        type = "model_select", model = CodingAgentExtensionSessionBridge.SerializeExtensionModel(Model),
        previousModel = CodingAgentExtensionSessionBridge.SerializeExtensionModel(previous), source
    });
}

public sealed partial class CodingAgentExtensionLifecycleEventSink
{
    /// <summary>【CodingAgent】【状态通知】按模块顺序等待通知处理器，错误隔离且拒绝向重载后的实例投递旧事件。</summary>
    /// <param name="payload">状态事件。</param><param name="reportError">错误接收器。</param>
    /// <param name="token">取消信号。</param><param name="generation">事件产生时的代次。</param>
    /// <returns>通知完成任务。</returns>
    internal async Task EmitNotificationAsync(JsonElement payload, Action<CodingAgentExtensionLifecycleEventError> reportError,
        CancellationToken token, long generation)
    {
        var type = payload.GetProperty("type").GetString()!;
        foreach (var module in _modules.Where(module => module.EventTypes.Contains(type)))
        {
            if (generation != _runtime.ResetGeneration) break;
            var result = await _runtime.EmitEventAsync(module.FilePath, payload, token, generation).ConfigureAwait(false);
            if (!result.Success) reportError(new(module.FilePath, module.Scope, module.Runtime, type, result.Error ?? "Extension notification failed."));
            foreach (var error in result.HandlerErrors) reportError(new(module.FilePath, module.Scope, module.Runtime, type, error));
        }
    }
}
