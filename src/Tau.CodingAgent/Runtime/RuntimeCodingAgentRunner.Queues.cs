// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【队列通知】公布仍待开始处理的用户输入。</summary>
/// <param name="Steering">引导输入文本。</param><param name="FollowUp">跟进输入文本。</param>
public sealed record CodingAgentQueueUpdateEvent(IReadOnlyList<string> Steering, IReadOnlyList<string> FollowUp) : AgentEvent("queue_update");

public sealed partial class RuntimeCodingAgentRunner
{
    private readonly object _pendingInputGate = new();
    private readonly List<UserMessage> _steeringInputs = [];
    private readonly List<UserMessage> _followUpInputs = [];
    private readonly object _queueNotificationGate = new();
    private Task _queueNotifications = Task.CompletedTask;
    private bool _queueNotificationsClosed;

    /// <summary>【CodingAgent】【待处理输入】尚未发布 message_start 的用户输入数量，不计自定义上下文消息。</summary>
    public int PendingMessageCount { get { lock (_pendingInputGate) return _steeringInputs.Count + _followUpInputs.Count; } }

    /// <summary>【CodingAgent】【引导查询】读取独立文本快照，不受单回合消费模式影响。</summary><returns>待处理引导输入。</returns>
    public IReadOnlyList<string> GetSteeringMessages() { lock (_pendingInputGate) return _steeringInputs.Select(QueuedMessageToText).ToArray(); }

    /// <summary>【CodingAgent】【跟进查询】读取独立文本快照，不受单回合消费模式影响。</summary><returns>待处理跟进输入。</returns>
    public IReadOnlyList<string> GetFollowUpMessages() { lock (_pendingInputGate) return _followUpInputs.Select(QueuedMessageToText).ToArray(); }

    /// <summary>【CodingAgent】【队列初始化】接管调用方已经排入底层运行器的用户消息。</summary>
    private void InitializeQueuedInputs()
    {
        var queued = _runtime.GetQueuedMessages();
        lock (_pendingInputGate) { _steeringInputs.AddRange(queued.Steering.OfType<UserMessage>()); _followUpInputs.AddRange(queued.FollowUp.OfType<UserMessage>()); }
    }

    /// <summary>【CodingAgent】【统一入队】同时维护底层队列和用户列表，冻结通知快照。</summary>
    /// <param name="message">已处理输入或自定义消息。</param><param name="followUp">是否进入跟进队列。</param>
    private void EnqueuePreparedInput(ChatMessage message, bool followUp)
    {
        lock (_pendingInputGate)
        {
            if (followUp) _runtime.FollowUp(message); else _runtime.Steer(message);
            if (message is not UserMessage user) return;
            (followUp ? _followUpInputs : _steeringInputs).Add(user);
            NotifyQueueUpdateLocked();
        }
    }

    /// <summary>【CodingAgent】【队列消费】逐条移除开始处理的输入，批量调度时其他输入仍保持可见。</summary>
    /// <param name="message">即将发布开始事件的用户消息。</param>
    private void ConsumeQueuedInput(UserMessage message)
    {
        lock (_pendingInputGate)
        {
            // 1. 【CodingAgent】【消息身份】优先按引用匹配，正确移除纯图片及同文本消息
            var steering = _steeringInputs.FindIndex(input => ReferenceEquals(input, message));
            var followUp = steering < 0 ? _followUpInputs.FindIndex(input => ReferenceEquals(input, message)) : -1;
            if (steering < 0 && followUp < 0 && QueuedMessageToText(message) is { Length: > 0 } text)
            {
                steering = _steeringInputs.FindIndex(input => QueuedMessageToText(input) == text);
                if (steering < 0) followUp = _followUpInputs.FindIndex(input => QueuedMessageToText(input) == text);
            }
            if (steering >= 0) _steeringInputs.RemoveAt(steering);
            else if (followUp >= 0) _followUpInputs.RemoveAt(followUp);
            else return;
            NotifyQueueUpdateLocked();
        }
    }

    /// <summary>【CodingAgent】【运行重置】同一同步边界清除底层状态与用户可见队列。</summary>
    private void ResetRuntimeState()
    {
        lock (_pendingInputGate) { _runtime.Reset(); _steeringInputs.Clear(); _followUpInputs.Clear(); NotifyQueueUpdateLocked(); }
    }

    /// <summary>【CodingAgent】【清空输入】移除所有队列；返回用户输入，自定义上下文消息不会回填编辑器。</summary>
    /// <returns>引导和跟进输入的独立文本数组。</returns>
    public CodingAgentQueuedMessages DrainQueuedMessages()
    {
        lock (_pendingInputGate)
        {
            var queued = new CodingAgentQueuedMessages(_steeringInputs.Select(QueuedMessageToText).ToArray(), _followUpInputs.Select(QueuedMessageToText).ToArray());
            _runtime.ClearAllQueues(); _steeringInputs.Clear(); _followUpInputs.Clear(); NotifyQueueUpdateLocked();
            return queued;
        }
    }

    /// <summary>【CodingAgent】【通知排队】持有输入锁时冻结快照，串行通知宿主，不调用扩展事件处理器。</summary>
    private void NotifyQueueUpdateLocked()
    {
        var notification = new CodingAgentQueueUpdateEvent(_steeringInputs.Select(QueuedMessageToText).ToArray(), _followUpInputs.Select(QueuedMessageToText).ToArray());
        var generation = Interlocked.Read(ref _stateBindingGeneration);
        lock (_queueNotificationGate)
        {
            if (_queueNotificationsClosed) return;
            var previous = _queueNotifications;
            // 1. 【CodingAgent】【通知完成】排空或关闭的延续不能阻塞仍等待当前通知的后继任务
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queueNotifications = completion.Task;
            _ = Task.Run(async () =>
            {
                try
                {
                    try { await previous.ConfigureAwait(false); } catch (Exception error) { LogRunError(error, CreateRunLogContext()); }
                    // 2. 【CodingAgent】【会话切换】同一绑定的清空快照必须送达，不能因会话标识已切换而丢失
                    if (generation != Interlocked.Read(ref _stateBindingGeneration)) return;
                    await PublishBackgroundEventAsync(notification, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { completion.TrySetResult(); }
            });
        }
    }

    /// <summary>【CodingAgent】【队列通知等待】消息开始、RPC 响应和会话释放前等待已经排入的快照。</summary><returns>通知完成任务。</returns>
    internal Task WaitForQueueNotificationsAsync() { lock (_queueNotificationGate) return _queueNotifications; }

    /// <summary>【CodingAgent】【队列通知开关】重新绑定时启用通知，关闭时阻止继续新增任务。</summary><param name="enabled">是否允许通知。</param>
    private void SetQueueNotificationsEnabled(bool enabled) { lock (_queueNotificationGate) _queueNotificationsClosed = !enabled; }
}
