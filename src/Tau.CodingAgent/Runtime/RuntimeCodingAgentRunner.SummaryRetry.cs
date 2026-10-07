// 作者：xxx
using Tau.AgentCore;
using Tau.Ai.Utilities;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【摘要退避】通知摘要暂时失败后的等待计划。</summary>
/// <param name="Attempt">当前重试次数。</param>
/// <param name="MaxAttempts">最大重试次数。</param>
/// <param name="DelayMs">等待毫秒数。</param>
/// <param name="ErrorMessage">提供方错误。</param>
public sealed record CodingAgentSummaryRetryScheduledEvent(int Attempt, int MaxAttempts, int DelayMs, string ErrorMessage)
    : AgentEvent("summarization_retry_scheduled");

/// <summary>【CodingAgent】【摘要重试】通知摘要即将重新请求。</summary>
/// <param name="Source">compaction 或 branchSummary。</param>
/// <param name="Reason">压缩原因，分支摘要时为空。</param>
public sealed record CodingAgentSummaryRetryAttemptEvent(string Source, string? Reason)
    : AgentEvent("summarization_retry_attempt_start");

/// <summary>【CodingAgent】【摘要重试结束】通知宿主清除摘要重试指示器。</summary>
public sealed record CodingAgentSummaryRetryFinishedEvent() : AgentEvent("summarization_retry_finished");

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【摘要策略】使用当前会话的摘要重试次数和等待预算。</summary>
    public CodingAgentRetryOptions SummaryRetryOptions { get; set; } = CodingAgentRetryOptions.Disabled;

    /// <summary>【CodingAgent】【摘要通知】把共享重试回调连接到交互、RPC 和打印宿主。</summary>
    /// <param name="source">摘要来源。</param>
    /// <param name="reason">可选压缩触发原因。</param>
    /// <returns>完整重试回调。</returns>
    private AssistantRetryCallbacks CreateSummaryRetryCallbacks(string source, string? reason = null) => new()
    {
        OnRetryScheduled = (attempt, maximum, delay, error) => PublishBackgroundEventAsync(new CodingAgentSummaryRetryScheduledEvent(attempt, maximum, delay, error), CancellationToken.None),
        OnRetryAttemptStart = () => PublishBackgroundEventAsync(new CodingAgentSummaryRetryAttemptEvent(source, reason), CancellationToken.None),
        OnRetryFinished = (_, _, _) => PublishBackgroundEventAsync(new CodingAgentSummaryRetryFinishedEvent(), CancellationToken.None)
    };
}
