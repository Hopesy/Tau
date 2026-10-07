// 作者：xxx
using System.Text.RegularExpressions;

namespace Tau.Ai.Utilities;

/// <summary>【Ai】【助手重试】定义额外重试次数、指数退避和单次等待上限。</summary>
/// <param name="Enabled">是否启用重试。</param>
/// <param name="MaxRetries">初次调用之后最多追加的次数。</param>
/// <param name="BaseDelayMilliseconds">第一次退避时间。</param>
/// <param name="MaxAgentDelayMilliseconds">等待上限，默认 60 秒。</param>
public sealed record AssistantRetryPolicy(bool Enabled, int MaxRetries, int BaseDelayMilliseconds,
    int MaxAgentDelayMilliseconds = 60_000);

/// <summary>【Ai】【重试通知】提供等待、重试开始和最终完成的异步通知。</summary>
public sealed record AssistantRetryCallbacks
{
    public Func<int, int, int, string, Task>? OnRetryScheduled { get; init; }
    public Func<Task>? OnRetryAttemptStart { get; init; }
    public Func<bool, int, string?, Task>? OnRetryFinished { get; init; }
}

/// <summary>【Ai】【助手重试】共享助手错误分类和支持协作取消的重试循环。</summary>
public static partial class AssistantRetry
{
    /// <summary>【Ai】【重试分类】识别暂时性的提供方错误，并优先排除账户、计费和配额耗尽。</summary>
    /// <param name="message">已完成的助手响应。</param>
    /// <returns>是否可按调用方策略重试。</returns>
    public static bool IsRetryableAssistantError(AssistantMessage message) => message.StopReason == StopReason.Error
        && IsRetryableError(message.ErrorMessage);

    /// <summary>【Ai】【错误分类】判断错误文本是否属于短暂故障。</summary>
    /// <param name="error">错误文本。</param>
    /// <returns>是否为可重试错误。</returns>
    public static bool IsRetryableError(string? error) => !string.IsNullOrEmpty(error)
        && !NonRetryablePattern().IsMatch(error) && RetryablePattern().IsMatch(error);

    /// <summary>【Ai】【退避计算】计算有上限的指数等待，避免大次数溢出。</summary>
    /// <param name="policy">重试策略。</param>
    /// <param name="attempt">从 1 开始的重试次数。</param>
    /// <returns>非负等待毫秒数。</returns>
    public static int RetryDelayMilliseconds(AssistantRetryPolicy policy, int attempt)
    {
        var delay = Math.Max(0, policy.BaseDelayMilliseconds) * Math.Pow(2, Math.Max(0, (double)attempt - 1));
        return (int)Math.Min(double.IsNaN(delay) ? 0 : delay, Math.Max(0, policy.MaxAgentDelayMilliseconds));
    }

    /// <summary>【Ai】【重试执行】对返回错误消息的助手请求执行有限重试，退避取消统一返回 aborted 响应。</summary>
    /// <param name="produce">一次助手请求，抛出的异常原样传播。</param>
    /// <param name="policy">可选策略；为空或禁用时只执行一次。</param>
    /// <param name="cancellationToken">退避取消信号，调用方也应将其传给请求。</param>
    /// <param name="callbacks">可选阶段通知。</param>
    /// <returns>最终助手响应，保留最后响应的内容、元数据与用量。</returns>
    public static async Task<AssistantMessage> RetryAssistantCallAsync(Func<Task<AssistantMessage>> produce,
        AssistantRetryPolicy? policy = null, CancellationToken cancellationToken = default, AssistantRetryCallbacks? callbacks = null)
    {
        ArgumentNullException.ThrowIfNull(produce);
        var maxAttempts = policy?.Enabled == true ? Math.Max(0, policy.MaxRetries) : 0;
        var attempt = 0;
        while (true)
        {
            var response = await produce().ConfigureAwait(false);
            // 1. 【Ai】【重试终值】中止、成功和确定性错误结束循环，只有实际安排过重试才发布结束通知
            if (response.StopReason == StopReason.Aborted)
            {
                if (attempt > 0 && callbacks?.OnRetryFinished is { } finished) await finished(false, attempt, null).ConfigureAwait(false);
                return response;
            }
            if (response.StopReason != StopReason.Error)
            {
                if (attempt > 0 && callbacks?.OnRetryFinished is { } finished) await finished(true, attempt, null).ConfigureAwait(false);
                return response;
            }
            if (attempt >= maxAttempts || !IsRetryableAssistantError(response))
            {
                if (attempt > 0 && callbacks?.OnRetryFinished is { } finished) await finished(false, attempt, response.ErrorMessage).ConfigureAwait(false);
                return response;
            }
            attempt++;
            var error = response.ErrorMessage ?? "Unknown error";
            var delay = RetryDelayMilliseconds(policy!, attempt);
            if (callbacks?.OnRetryScheduled is { } scheduled) await scheduled(attempt, maxAttempts, delay, error).ConfigureAwait(false);
            // 2. 【Ai】【退避取消】取消等待时保留响应字段，删除错误文本并改为 aborted，避免继续调用提供方
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (callbacks?.OnRetryFinished is { } finished) await finished(false, attempt, error).ConfigureAwait(false);
                return response with { StopReason = StopReason.Aborted, ErrorMessage = null };
            }
            if (callbacks?.OnRetryAttemptStart is { } started) await started().ConfigureAwait(false);
        }
    }

    /// <summary>【Ai】【限额分类】匹配账户限额和计费失败的上游规则。</summary>
    /// <returns>非重试错误表达式。</returns>
    [GeneratedRegex("GoUsageLimitError|FreeUsageLimitError|Monthly usage limit reached|available balance|insufficient_quota|out of budget|quota exceeded|billing|subscription_sharing_usage_limit_exceeded", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonRetryablePattern();

    /// <summary>【Ai】【暂时故障】匹配 HTTP、流中断、DNS、WebSocket 和明确重试提示。</summary>
    /// <returns>可重试错误表达式。</returns>
    [GeneratedRegex("overloaded|currently experiencing high demand|model is at capacity|rate.?limit|too many requests|429|500|502|503|504|520|524|service.?unavailable|server.?error|internal.?error|provider.?returned.?error|exceeded request buffer limit while retrying upstream|network.?error|connection.?error|connection.?refused|connection.?lost|other side closed|fetch failed|getaddrinfo|ENOTFOUND|EAI_AGAIN|upstream.?connect|reset before headers|socket hang up|socket connection was closed|timed? out|timeout|terminated|websocket.?closed|websocket.?error|ended without|stream ended before message_stop|stream ended before a terminal response event|http2 request did not get a response|retry delay|you can retry your request|try your request again|please retry your request|ResourceExhausted|subscription_sharing_usage_unavailable|subscription_sharing_user_unavailable", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetryablePattern();
}
