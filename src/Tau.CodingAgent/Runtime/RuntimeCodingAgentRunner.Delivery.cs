// 作者：xxx
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.Ai.Observability;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private readonly ConcurrentQueue<CodingAgentExtensionMessageDelivery> _extensionDeliveries = new();
    private readonly Queue<AgentCustomMessage> _deferredExtensionMessages = new();
    private readonly Queue<AgentCustomMessage> _nextTurnExtensionMessages = new();
    private readonly Queue<AgentEvent> _extensionMessageEvents = new();
    private readonly List<ChatMessage> _extensionRunMessages = [];
    private readonly HashSet<AgentEvent> _publishedExtensionMessageEvents = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _backgroundGate = new();
    private CancellationTokenSource? _backgroundLifetime;
    private Task? _backgroundTask;
    private CancellationTokenSource? _currentRunCancellation;

    /// <summary>【CodingAgent】【后台事件】发布扩展自动运行的事件；普通 RunAsync 事件仍由调用方枚举。</summary>
    public event Func<AgentEvent, CancellationToken, Task>? BackgroundEvent;

    /// <summary>【CodingAgent】【后台调度】宿主完成事件订阅后启用自动消息处理。</summary>
    public void StartBackgroundDelivery()
    {
        lock (_backgroundGate) _backgroundLifetime ??= new();
        ScheduleBackgroundDelivery();
    }

    /// <summary>【CodingAgent】【后台停止】取消自动回合并等待其退出，停止后保留普通运行能力。</summary>
    /// <returns>后台任务和订阅回调全部退出后完成的任务。</returns>
    public Task StopBackgroundDeliveryAsync()
    {
        CancellationTokenSource? lifetime;
        Task? pending;
        lock (_backgroundGate)
        {
            lifetime = _backgroundLifetime;
            _backgroundLifetime = null;
            pending = _backgroundTask;
            _extensionDeliveries.Clear();
        }
        if (lifetime is null) return pending ?? Task.CompletedTask;
        return StopBackgroundCoreAsync(lifetime, pending);
    }

    /// <summary>【CodingAgent】【后台释放】在锁外取消任务，防止取消回调重入运行器锁。</summary>
    /// <param name="lifetime">此次后台调度的生命周期。</param>
    /// <param name="pending">正在运行或等待运行的后台工作。</param>
    /// <returns>清理完成的任务。</returns>
    private static async Task StopBackgroundCoreAsync(CancellationTokenSource lifetime, Task? pending)
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (pending is not null) await pending.ConfigureAwait(false);
        lifetime.Dispose();
    }

    /// <summary>【CodingAgent】【运行互斥】串行执行完整准备、模型和扩展生命周期，防止空闲投递抢占用户运行。</summary>
    /// <param name="inner">延迟执行的 Agent 事件序列。</param>
    /// <param name="inputBytes">输入大小。</param>
    /// <param name="logContext">运行日志上下文。</param>
    /// <param name="cancellationToken">请求取消信号。</param>
    /// <returns>包含扩展主动消息的事件序列。</returns>
    private async IAsyncEnumerable<AgentEvent> InstrumentRun(IAsyncEnumerable<AgentEvent> inner, int inputBytes,
        TauRuntimeLogContext logContext, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_backgroundGate) _currentRunCancellation = current;
        try
        {
            await foreach (var evt in RunUntilSettledAsync(inner, inputBytes, logContext, current.Token).ConfigureAwait(false)) yield return evt;
        }
        finally
        {
            _boundaryDispatchedMessages.Clear();
            try { FlushPendingBashMessages(finishRun: true); }
            finally
            {
                lock (_backgroundGate) _currentRunCancellation = null;
                _runGate.Release();
                ScheduleBackgroundDelivery();
            }
        }
    }

    /// <summary>【CodingAgent】【后台唤醒】合并空闲消息唤醒，活动运行优先在自身事件边界消费消息。</summary>
    private void ScheduleBackgroundDelivery()
    {
        lock (_backgroundGate)
        {
            if (IsShutdownRequested || _backgroundLifetime is null || _backgroundTask is not null || _extensionDeliveries.IsEmpty || _runGate.CurrentCount == 0 || IsCompacting) return;
            var lifetime = _backgroundLifetime;
            _backgroundTask = Task.Run(() => RunBackgroundDeliveryAsync(lifetime));
        }
    }

    /// <summary>【CodingAgent】【后台执行】处理空闲主动消息，隔离事件订阅错误并防止漏掉最后一次唤醒。</summary>
    /// <param name="lifetime">所属调度生命周期。</param>
    /// <returns>当前积压消息处理完毕后结束的任务。</returns>
    private async Task RunBackgroundDeliveryAsync(CancellationTokenSource lifetime)
    {
        var token = lifetime.Token;
        var ended = false;
        var firstMessage = Messages.Count;
        try
        {
            while (!IsShutdownRequested && !_extensionDeliveries.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                ended = false;
                firstMessage = Messages.Count;
                var context = CreateRunLogContext();
                await foreach (var evt in InstrumentRun(RunPreparedAsync([], context, token), 0, context, token).ConfigureAwait(false))
                {
                    if (evt is AgentEndEvent) ended = true;
                    try
                    {
                        await PublishBackgroundEventAsync(evt, evt is AgentEndEvent or CodingAgentSettledEvent ? CancellationToken.None : token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // 1. 【CodingAgent】【取消收尾】中间订阅被取消后继续消费终态，避免丢失结束及结算事件
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!ended) await PublishBackgroundEventAsync(new AgentEndEvent("Request aborted", Messages.Skip(firstMessage).ToArray()), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRunError(ex, CreateRunLogContext());
            if (!ended) await PublishBackgroundEventAsync(new AgentEndEvent(ex.Message, Messages.Skip(firstMessage).ToArray()), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_backgroundGate) _backgroundTask = null;
            ScheduleBackgroundDelivery();
        }
    }

    /// <summary>【CodingAgent】【后台订阅】按订阅顺序通知宿主，单个订阅错误不阻止持久化和其他宿主。</summary>
    /// <param name="evt">要发布的后台事件。</param>
    /// <param name="token">运行取消信号；结束兜底使用不可取消信号。</param>
    /// <returns>所有订阅处理完成的任务。</returns>
    private async Task PublishBackgroundEventAsync(AgentEvent evt, CancellationToken token)
    {
        if (BackgroundEvent is not { } handlers) return;
        foreach (Func<AgentEvent, CancellationToken, Task> handler in handlers.GetInvocationList())
        {
            try { await handler(evt, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { LogRunError(ex, CreateRunLogContext()); }
        }
    }

    /// <summary>【CodingAgent】【主动消息】接收事件和工具发送动作，等待安全的回合边界处理。</summary>
    /// <param name="delivery">保留类型、图片和投递选项的消息。</param>
    internal void EnqueueExtensionMessage(CodingAgentExtensionMessageDelivery delivery)
    {
        _extensionDeliveries.Enqueue(delivery);
        ScheduleBackgroundDelivery();
    }

    /// <summary>【CodingAgent】【消息调度】处理主动用户输入及自定义投递，不把仅追加消息插入工具调用与结果之间。</summary>
    /// <param name="running">是否位于正在执行的 Agent 回合。</param>
    /// <param name="token">当前运行取消信号。</param>
    /// <param name="completedTurn">是否正在处理 agent_end 后的投递；指定队列的消息仍属于当前用户运行。</param>
    /// <returns>空闲时需要启动回合的消息。</returns>
    private IReadOnlyList<ChatMessage> DrainExtensionDeliveries(bool running, CancellationToken token, bool completedTurn = false)
    {
        var initial = new List<ChatMessage>();
        while (_extensionDeliveries.TryDequeue(out var delivery))
        {
            token.ThrowIfCancellationRequested();
            var queueForCurrentRun = running || completedTurn && delivery.DeliverAs is "steer" or "followUp";
            if (delivery.Message is UserMessage)
            {
                if (PrepareInputMessage(delivery.Message, queueForCurrentRun ? delivery.DeliverAs : null, token,
                    source: "extension", expandTemplates: delivery.ExpandPromptTemplates) is not { } user) continue;
                if (queueForCurrentRun && delivery.DeliverAs is not ("steer" or "followUp"))
                {
                    LogExtensionEventError(new(delivery.FilePath, "extension", "javascript", "sendUserMessage",
                        "Agent is already processing. Specify deliverAs ('steer' or 'followUp') to queue the message."), CreateRunLogContext());
                    continue;
                }
                if (!queueForCurrentRun) initial.Add(user);
                else EnqueuePreparedInput(user, delivery.DeliverAs == "followUp");
                continue;
            }
            if (delivery.Message is not AgentCustomMessage custom) continue;
            if (delivery.DeliverAs == "nextTurn") _nextTurnExtensionMessages.Enqueue(custom);
            else if (queueForCurrentRun && delivery.TriggerTurn != false)
            {
                EnqueuePreparedInput(custom, delivery.DeliverAs == "followUp");
            }
            else if (delivery.TriggerTurn == true) initial.Add(custom);
            else if (running) _deferredExtensionMessages.Enqueue(custom);
            else AppendExtensionCustomMessage(custom, token);
        }
        return initial;
    }

    /// <summary>【CodingAgent】【仅追加消息】工具结果提交之后记录消息，并准备对宿主发布消息事件。</summary>
    /// <param name="token">当前运行取消信号。</param>
    private void FlushDeferredExtensionMessages(CancellationToken token)
    {
        while (_deferredExtensionMessages.TryDequeue(out var message)) AppendExtensionCustomMessage(message, token);
    }

    /// <summary>【CodingAgent】【自定义历史】追加消息并保持开始、结束事件成对出现。</summary>
    /// <param name="message">待追加的自定义消息。</param>
    /// <param name="token">当前运行取消信号。</param>
    private void AppendExtensionCustomMessage(AgentCustomMessage message, CancellationToken token)
    {
        // 1. 【CodingAgent】【消息事件】先通知开始，再执行与普通消息相同的结束转换
        var start = new MessageStartEvent(message);
        PublishLifecycleEventAsync(start, token).GetAwaiter().GetResult();
        var transformed = TransformLifecycleMessageEndAsync(_config.MessageEndTransformAsync, message, CreateRunLogContext(), token)
            .GetAwaiter().GetResult();
        var final = transformed as AgentCustomMessage ?? message;
        _runtime.AddMessage(final);
        var end = new MessageEndEvent(final);
        // 2. 【CodingAgent】【事件去重】宿主仍接收完整事件，但不向扩展重复发布已经执行的处理器
        _publishedExtensionMessageEvents.Add(start);
        _publishedExtensionMessageEvents.Add(end);
        _extensionMessageEvents.Enqueue(start);
        _extensionMessageEvents.Enqueue(end);
    }

    /// <summary>【CodingAgent】【消息清理】重置或切换会话时丢弃上一会话尚未消费的主动消息。</summary>
    private void ClearExtensionDeliveries()
    {
        lock (_nestedToolGate) _nestedToolScopes.Clear();
        _extensionDeliveries.Clear();
        _deferredExtensionMessages.Clear();
        _nextTurnExtensionMessages.Clear();
        _extensionMessageEvents.Clear();
        _extensionRunMessages.Clear();
        _publishedExtensionMessageEvents.Clear();
    }
}
