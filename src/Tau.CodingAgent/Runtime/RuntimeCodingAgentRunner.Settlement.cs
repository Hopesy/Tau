// 作者：xxx
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai.Observability;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【运行结算】通知宿主一次用户运行及其重试、队列和结算边界均已完成</summary>
public sealed record CodingAgentSettledEvent() : AgentEvent("agent_settled");

public sealed partial class RuntimeCodingAgentRunner
{
    private AgentLoopConfig? _settlementContinuationConfig;
    private RecoveryState? _settlementRecovery;
    private bool _isEmittingAgentSettled;

    /// <summary>【CodingAgent】【结算编排】等待恢复和主动投递完成，执行结算草稿后才发布真正空闲事件</summary>
    /// <param name="inner">已经取得运行锁的初始事件流</param><param name="inputBytes">输入大小</param>
    /// <param name="logContext">本次运行日志上下文</param><param name="token">整个用户运行的取消信号</param>
    /// <returns>包含边界提交、继续运行及最终 agent_settled 的事件流</returns>
    private async IAsyncEnumerable<AgentEvent> RunUntilSettledAsync(IAsyncEnumerable<AgentEvent> inner, int inputBytes,
        TauRuntimeLogContext logContext, [EnumeratorCancellation] CancellationToken token)
    {
        var source = InstrumentRunCore(inner, inputBytes, logContext, token);
        var started = false;
        var settled = false;
        var attemptEnded = false;
        var firstMessage = Messages.Count;
        try
        {
            while (true)
            {
                ExceptionDispatchInfo? failure = null;
                // 1. 【CodingAgent】【活动结束】保留枚举异常，先通知结算再向调用方传播原异常
                await using (var iterator = source.GetAsyncEnumerator(token))
                {
                    while (true)
                    {
                        var hasNext = false;
                        try { hasNext = await iterator.MoveNextAsync().ConfigureAwait(false); }
                        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
                        if (!hasNext) break;
                        if (iterator.Current is AgentStartEvent)
                        {
                            started = true;
                            attemptEnded = false;
                            firstMessage = Messages.Count;
                        }
                        if (iterator.Current is AgentEndEvent) attemptEnded = true;
                        yield return iterator.Current;
                    }
                }
                if (started && failure is null && !token.IsCancellationRequested && !IsShutdownRequested &&
                    await DispatchBeforeSettleAsync(logContext).ConfigureAwait(false) && !token.IsCancellationRequested && !IsShutdownRequested)
                {
                    // 2. 【CodingAgent】【结算继续】沿用原提示和恢复预算，不重新执行 input 或 before_agent_start
                    var config = CreateRecoveryContinuation(_settlementContinuationConfig ?? WithAttributionHeaders(logContext));
                    source = InstrumentRunCore(RunRecoverableAsync(config, _settlementRecovery ?? new RecoveryState(), token), 0, logContext, token);
                    continue;
                }
                if (started)
                {
                    if (failure is not null && !attemptEnded)
                    {
                        var error = failure.SourceException is OperationCanceledException ? "Request aborted" : failure.SourceException.Message;
                        FlushPendingBashMessages();
                        var ended = new AgentEndEvent(error, Messages.Skip(firstMessage).ToArray());
                        await PublishLifecycleEventAsync(ended, CancellationToken.None).ConfigureAwait(false);
                        yield return ended;
                    }
                    // 3. 【CodingAgent】【命令结算】异常结束同样必须在结算通知之前提交生成期间完成的命令
                    FlushPendingBashMessages();
                    FlushBoundaryCustomMessages();
                    while (_extensionMessageEvents.TryDequeue(out var appended))
                    {
                        _publishedExtensionMessageEvents.Remove(appended);
                        yield return appended;
                    }
                    _runSystemPromptOptions = null;
                    _cacheWarmer?.OnAgentSettled();
                    _isEmittingAgentSettled = true;
                    settled = true;
                    try
                    {
                        var notification = new CodingAgentSettledEvent();
                        await PublishLifecycleEventAsync(notification, CancellationToken.None).ConfigureAwait(false);
                        yield return notification;
                    }
                    finally { _isEmittingAgentSettled = false; }
                }
                failure?.Throw();
                if (!started || token.IsCancellationRequested || IsShutdownRequested) yield break;
                // 3. 【CodingAgent】【结算投递】结算处理器触发的新提示在通知完成后执行，空闲等待涵盖这些后续动作
                var next = DrainExtensionDeliveries(false, token);
                while (_extensionMessageEvents.TryDequeue(out var appended))
                {
                    _publishedExtensionMessageEvents.Remove(appended);
                    yield return appended;
                }
                if (next.Count == 0) yield break;
                started = false;
                settled = false;
                source = InstrumentRunCore(RunPreparedAsync(next, logContext, token, inputPrepared: true), next.Sum(EstimateInputBytes), logContext, token);
            }
        }
        finally
        {
            // 4. 【CodingAgent】【提前释放】消费者中止枚举时仍结束扩展生命周期，公开事件由正常枚举路径交付
            try
            {
                if (started && !settled)
                {
                    FlushBoundaryCustomMessages();
                    _runSystemPromptOptions = null;
                    _cacheWarmer?.OnAgentSettled();
                    _isEmittingAgentSettled = true;
                    try { await PublishLifecycleEventAsync(new CodingAgentSettledEvent(), CancellationToken.None).ConfigureAwait(false); }
                    finally { _isEmittingAgentSettled = false; }
                }
            }
            finally
            {
                _settlementContinuationConfig = null;
                _settlementRecovery = null;
                _runSystemPromptOptions = null;
            }
        }
    }

    /// <summary>【CodingAgent】【结算边界】复用回合草稿协议，允许最终上下文编辑或排队触发继续</summary>
    /// <param name="logContext">运行日志上下文</param><returns>是否还需要一次模型运行</returns>
    private async Task<bool> DispatchBeforeSettleAsync(TauRuntimeLogContext logContext)
    {
        if (_boundarySession is null || _extensionLifecycleEventSink?.HasBoundaryHandlers("agent_before_settle") != true)
            return _runtime.HasQueuedMessages;
        try
        {
            _boundarySession.BeginBoundary();
            return await DispatchBoundaryDraftsAsync(new JsonObject { ["type"] = "agent_before_settle", ["outcome"] = _lastBoundaryOutcome }, logContext).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogExtensionEventError(new("<boundary>", "extension", "javascript", "agent_before_settle", exception.Message), logContext);
            return false;
        }
        finally { _boundarySession.EndBoundary(); }
    }

    /// <summary>【CodingAgent】【结算消息】结算前保存暂存的自定义消息，同时产生正常消息生命周期</summary>
    private void FlushBoundaryCustomMessages()
    {
        FlushDeferredExtensionMessages(CancellationToken.None);
    }
}
