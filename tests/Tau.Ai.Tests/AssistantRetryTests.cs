// 作者：xxx
using Tau.Ai.Utilities;

namespace Tau.Ai.Tests;

public sealed class AssistantRetryTests
{
    /// <summary>暂时故障可以重试，账户限额即使含 429 或服务器错误也必须立即返回。</summary>
    /// <param name="error">提供方错误文本。</param>
    /// <param name="expected">是否可重试。</param>
    [Theory]
    [InlineData("currently experiencing high demand", true)]
    [InlineData("model is at capacity", true)]
    [InlineData("520 status code", true)]
    [InlineData("524 status code", true)]
    [InlineData("getaddrinfo ENOTFOUND service", true)]
    [InlineData("EAI_AGAIN service", true)]
    [InlineData("socket connection was closed unexpectedly", true)]
    [InlineData("stream ended before a terminal response event", true)]
    [InlineData("stream ended before message_stop", true)]
    [InlineData("HTTP2 request did not get a response", true)]
    [InlineData("WebSocket closed", true)]
    [InlineData("You can retry your request", true)]
    [InlineData("ResourceExhausted: Worker local total request limit reached", true)]
    [InlineData("subscription_sharing_usage_unavailable", true)]
    [InlineData("subscription_sharing_user_unavailable", true)]
    [InlineData("429 quota exceeded", false)]
    [InlineData("429 insufficient_quota", false)]
    [InlineData("503 billing unavailable", false)]
    [InlineData("429 GoUsageLimitError", false)]
    [InlineData("429 FreeUsageLimitError", false)]
    [InlineData("429 available balance", false)]
    [InlineData("429 subscription_sharing_usage_limit_exceeded", false)]
    [InlineData("invalid API key", false)]
    public void Classifier_RecognizesTransientAndTerminalErrors(string error, bool expected)
    {
        Assert.Equal(expected, AssistantRetry.IsRetryableAssistantError(Failure(error)));
        Assert.False(AssistantRetry.IsRetryableAssistantError(Failure(error) with { StopReason = StopReason.EndTurn }));
        Assert.False(AssistantRetry.IsRetryableAssistantError(Failure(error) with { StopReason = StopReason.Aborted }));
    }

    /// <summary>指数退避遵守默认、定制和零等待上限。</summary>
    /// <param name="maximum">等待上限。</param>
    /// <param name="attempt">当前重试次数。</param>
    /// <param name="expected">预期等待毫秒数。</param>
    [Theory]
    [InlineData(60000, 6, 60000)]
    [InlineData(5000, 5, 5000)]
    [InlineData(0, 5, 0)]
    [InlineData(60000, 1000, 60000)]
    public void Delay_ClampsExponentialBackoff(int maximum, int attempt, int expected) =>
        Assert.Equal(expected, AssistantRetry.RetryDelayMilliseconds(new(true, 3, 2000, maximum), attempt));

    /// <summary>成功重试按等待、开始、完成的顺序通知且只返回最终响应。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Retry_ReportsOrderedCallbacksAndFinalSuccess()
    {
        var events = new List<string>();
        var calls = 0;
        var success = new AssistantMessage([new TextContent("success")]);
        var response = await AssistantRetry.RetryAssistantCallAsync(() =>
        {
            events.Add("call");
            return Task.FromResult(++calls < 3 ? Failure("terminated") : success);
        }, new(true, 3, 0), callbacks: new()
        {
            OnRetryScheduled = (attempt, max, delay, error) => { events.Add($"scheduled:{attempt}:{max}:{delay}:{error}"); return Task.CompletedTask; },
            OnRetryAttemptStart = () => { events.Add("start"); return Task.CompletedTask; },
            OnRetryFinished = (ok, attempt, error) => { events.Add($"finished:{ok}:{attempt}:{error}"); return Task.CompletedTask; }
        });
        Assert.Same(success, response);
        Assert.Equal(["call", "scheduled:1:3:0:terminated", "start", "call", "scheduled:2:3:0:terminated", "start", "call", "finished:True:2:"], events);
    }

    /// <summary>禁用策略直接返回原响应，不发送任何重试通知。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Retry_DisabledReturnsOriginalWithoutCallbacks()
    {
        var original = Failure("503 temporary");
        var called = 0;
        var actual = await AssistantRetry.RetryAssistantCallAsync(() => { called++; return Task.FromResult(original); }, new(false, 3, 0), callbacks: new()
        {
            OnRetryScheduled = (_, _, _, _) => throw new InvalidOperationException("unexpected callback"),
            OnRetryFinished = (_, _, _) => throw new InvalidOperationException("unexpected callback")
        });
        Assert.Same(original, actual);
        Assert.Equal(1, called);
    }

    /// <summary>重试预算耗尽或后续出现确定性错误时只发送一次失败结束通知。</summary>
    /// <param name="quota">第二次请求是否为账户配额错误。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_StopsOnExhaustionOrDeterministicError(bool quota)
    {
        var calls = 0;
        var finished = new List<(bool Success, int Attempt, string? Error)>();
        var response = await AssistantRetry.RetryAssistantCallAsync(() => Task.FromResult(Failure(++calls > 1 && quota ? "429 quota exceeded" : "503 transient")),
            new(true, 2, 0), callbacks: new() { OnRetryFinished = (success, attempt, error) => { finished.Add((success, attempt, error)); return Task.CompletedTask; } });
        Assert.Equal(quota ? 2 : 3, calls);
        var last = Assert.Single(finished);
        Assert.False(last.Success);
        Assert.Equal(quota ? 1 : 2, last.Attempt);
        Assert.Equal(response.ErrorMessage, last.Error);
    }

    /// <summary>退避取消返回保留内容和用量的 aborted 消息，不启动下一次请求。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Retry_CancellationDuringBackoffPreservesLastResponse()
    {
        using var cancellation = new CancellationTokenSource();
        var original = Failure("terminated") with { Content = [new TextContent("partial")], Usage = new(12, 3) };
        var calls = 0;
        var finished = 0;
        var response = await AssistantRetry.RetryAssistantCallAsync(() => { calls++; return Task.FromResult(original); }, new(true, 3, 60000), cancellation.Token,
            new()
            {
                OnRetryScheduled = (_, _, _, _) => { cancellation.Cancel(); return Task.CompletedTask; },
                OnRetryAttemptStart = () => throw new InvalidOperationException("unexpected retry"),
                OnRetryFinished = (success, attempt, error) => { Assert.False(success); Assert.Equal(1, attempt); Assert.Equal("terminated", error); finished++; return Task.CompletedTask; }
            });
        Assert.Equal(1, calls);
        Assert.Equal(1, finished);
        Assert.Equal(StopReason.Aborted, response.StopReason);
        Assert.Null(response.ErrorMessage);
        Assert.Equal(original.Content, response.Content);
        Assert.Equal(original.Usage, response.Usage);
    }

    /// <summary>提供方直接返回 aborted 时终止重试，完成通知不附带上一次错误。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Retry_ProviderAbortIsTerminal()
    {
        var calls = 0;
        var finished = new List<(bool, int, string?)>();
        var response = await AssistantRetry.RetryAssistantCallAsync(() => Task.FromResult(++calls == 1 ? Failure("terminated") : new AssistantMessage([]) { StopReason = StopReason.Aborted }),
            new(true, 3, 0), callbacks: new() { OnRetryFinished = (success, attempt, error) => { finished.Add((success, attempt, error)); return Task.CompletedTask; } });
        Assert.Equal(StopReason.Aborted, response.StopReason);
        Assert.Equal((false, 1, (string?)null), Assert.Single(finished));
        Assert.Equal(2, calls);
    }

    /// <summary>构造携带提供方错误的助手响应。</summary>
    /// <param name="error">错误文本。</param>
    /// <returns>助手失败响应。</returns>
    private static AssistantMessage Failure(string error) => new([]) { StopReason = StopReason.Error, ErrorMessage = error };
}
