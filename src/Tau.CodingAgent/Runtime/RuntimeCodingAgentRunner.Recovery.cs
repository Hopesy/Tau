// 作者：xxx
using System.Runtime.CompilerServices;
using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【回合重试】通知宿主即将等待下一次尝试。</summary>
/// <param name="Attempt">从一开始的额外尝试序号。</param>
/// <param name="MaxAttempts">最多允许的额外尝试次数。</param>
/// <param name="DelayMs">此次等待毫秒数。</param>
/// <param name="ErrorMessage">失败原因。</param>
public sealed record CodingAgentAutoRetryStartEvent(int Attempt, int MaxAttempts, int DelayMs, string ErrorMessage)
    : AgentEvent("auto_retry_start");

/// <summary>【CodingAgent】【回合重试】通知宿主重试结束。</summary>
/// <param name="Success">最后一次尝试是否成功。</param>
/// <param name="Attempt">已调度的额外尝试次数。</param>
/// <param name="FinalError">失败或取消原因。</param>
public sealed record CodingAgentAutoRetryEndEvent(bool Success, int Attempt, string? FinalError = null)
    : AgentEvent("auto_retry_end");

/// <summary>【CodingAgent】【记录追加】通知宿主新增的会话记录。</summary>
/// <param name="Entry">已保存的原始条目。</param>
public sealed record CodingAgentEntryAppendedEvent(System.Text.Json.JsonElement Entry) : AgentEvent("entry_appended");

public sealed partial class RuntimeCodingAgentRunner
{
    private CancellationTokenSource? _retryCancellation;
    private Func<IReadOnlyList<ChatMessage>, IReadOnlyList<CodingAgentTreeSessionEntry>>? _omitRecoveryMessages;

    /// <summary>【CodingAgent】【重试配置】普通回合与摘要共用的额外尝试预算和退避策略。</summary>
    public CodingAgentRetryOptions RetryOptions { get => SummaryRetryOptions; set => SummaryRetryOptions = value; }

    /// <summary>【CodingAgent】【重试状态】当前是否正在等待下一次尝试。</summary>
    public bool IsRetrying => Volatile.Read(ref _retryCancellation) is not null;

    /// <summary>【CodingAgent】【取消重试】只取消等待中的重试，不改变已经完成的历史记录。</summary>
    public void AbortRetry()
    {
        lock (_backgroundGate) _retryCancellation?.Cancel();
    }

    /// <summary>【CodingAgent】【恢复绑定】绑定失败消息的持久省略操作。</summary>
    /// <param name="omit">保存上下文编辑并刷新模型投影的回调。</param>
    internal void ConfigureRecoverySession(Func<IReadOnlyList<ChatMessage>, IReadOnlyList<CodingAgentTreeSessionEntry>> omit) => _omitRecoveryMessages = omit;

    /// <summary>【CodingAgent】【恢复记录】保留失败原文，仅从模型上下文移除指定消息。</summary>
    /// <param name="messages">需要省略的失败助手和工具消息。</param>
    /// <returns>已追加的上下文编辑事件。</returns>
    private IReadOnlyList<AgentEvent> OmitRecoveryMessages(IReadOnlyList<ChatMessage> messages)
    {
        if (_omitRecoveryMessages is { } omit)
            return omit(messages).Select(entry => (AgentEvent)new CodingAgentEntryAppendedEvent(
                System.Text.Json.JsonSerializer.SerializeToElement(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry))).ToArray();
        // 1. 【CodingAgent】【无存储恢复】直接构造的运行器没有会话日志，但仍需保留其余消息和等待队列
        _runtime.ReplaceMessages(Messages.Where(message => !messages.Any(target => ReferenceEquals(target, message))).ToArray());
        return [];
    }

    /// <summary>【CodingAgent】【回合恢复】在同一运行锁内重试失败请求，不重复输入变换、工具副作用或启动前钩子。</summary>
    /// <param name="config">已经完成启动准备的模型配置。</param>
    /// <param name="recovery">原始输入和后续恢复共用的溢出预算。</param>
    /// <param name="token">整个用户运行的取消信号。</param>
    /// <returns>每次尝试和恢复操作的完整事件流。</returns>
    private async IAsyncEnumerable<AgentEvent> RunRecoverableAsync(AgentLoopConfig config, RecoveryState recovery,
        [EnumeratorCancellation] CancellationToken token)
    {
        var attempt = 0;
        while (true)
        {
            AssistantMessage? lastAssistant = null;
            string? lastError = null;
            var retry = RetryOptions;
            var shouldRetry = false;
            IReadOnlyList<ToolResultMessage> toolResults = [];
            // 1. 【CodingAgent】【尝试结束】完整释放 Agent 枚举器后再修改上下文和继续运行
            await foreach (var evt in MergeNestedToolEventsAsync(_runtime.RunAsync(config, token), token).ConfigureAwait(false))
            {
                if (evt is TurnEndEvent turn) toolResults = turn.ToolResults;
                if (evt is AgentEndEvent end)
                {
                    lastAssistant = end.Messages.OfType<AssistantMessage>().LastOrDefault();
                    lastError = end.ErrorMessage;
                    shouldRetry = !token.IsCancellationRequested && retry.IsEnabled && attempt < retry.MaxAttempts &&
                        lastAssistant is { StopReason: StopReason.Error } &&
                        CodingAgentRetryClassifier.IsRetryable(lastAssistant.ErrorMessage, Model.ContextWindow ?? 0);
                    yield return end with { WillRetry = shouldRetry };
                }
                else yield return evt;
            }
            if (!shouldRetry)
            {
                if (attempt > 0)
                {
                    var success = lastAssistant is { StopReason: not (StopReason.Error or StopReason.Aborted) } && lastError is null;
                    yield return new CodingAgentAutoRetryEndEvent(success, attempt,
                        success ? null : lastError ?? lastAssistant?.ErrorMessage ?? "Retry cancelled");
                    attempt = 0;
                }
                await foreach (var evt in CheckAutomaticCompactionAsync(lastAssistant, toolResults, recovery, token).ConfigureAwait(false)) yield return evt;
                if (!token.IsCancellationRequested && (recovery.Continue || PendingMessageCount > 0))
                {
                    config = CreateRecoveryContinuation(config);
                    continue;
                }
                yield break;
            }

            // 2. 【CodingAgent】【退避取消】发布开始事件前建立取消源，订阅者可以立即调用 abortRetry
            attempt++;
            var delay = retry.GetDelay(attempt);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (_backgroundGate) _retryCancellation = cancellation;
            var cancelled = false;
            try
            {
                yield return new CodingAgentAutoRetryStartEvent(attempt, retry.MaxAttempts, (int)delay.TotalMilliseconds,
                    lastAssistant!.ErrorMessage ?? "Unknown error");
                foreach (var entry in OmitRecoveryMessages([lastAssistant])) yield return entry;
                try { await Task.Delay(delay, cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { cancelled = true; }
            }
            finally { lock (_backgroundGate) _retryCancellation = null; }
            if (cancelled)
            {
                yield return new CodingAgentAutoRetryEndEvent(false, attempt, "Retry cancelled");
                yield break;
            }
            // 3. 【CodingAgent】【继续请求】沿用本轮强制提示和请求钩子，只移除首次输入
            _failedVirtualResponse = lastAssistant;
            config = CreateRecoveryContinuation(config);
        }
    }

    /// <summary>【CodingAgent】【队列继续】已经完成的助手响应优先消费引导或后续消息，避免空转模型请求。</summary>
    /// <param name="config">当前请求配置。</param>
    /// <returns>包含正确首批队列消息的继续配置。</returns>
    private AgentLoopConfig CreateRecoveryContinuation(AgentLoopConfig config)
    {
        if (Messages.LastOrDefault() is AssistantMessage)
        {
            var steering = _runtime.DrainSteeringMessages();
            if (steering.Count > 0) return config with { InitialMessages = steering, SkipInitialSteeringPoll = true };
            return config with { InitialMessages = _runtime.DrainFollowUpMessages(), SkipInitialSteeringPoll = false };
        }
        return config with { InitialMessages = [], SkipInitialSteeringPoll = false };
    }
}
