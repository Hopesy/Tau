// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【预热回归】以可控时钟验证真实请求重放、费用与取消边界。</summary>
public sealed class CodingAgentCacheWarmerTests
{
    /// <summary>【CodingAgent】【预热显示】倒计时、负收益和费用精度与原生会话显示一致。</summary>
    [Fact]
    public void Formatter_ReportsDecisionAndUsage()
    {
        var now = DateTimeOffset.UnixEpoch;
        var decision = new CodingAgentCacheWarmingDecision("idle", .1m, .2m, .15m, -.07m, true, "stop");
        Assert.Equal("Decision in 1h 2m 1s (15% continuation probability, expected savings -$0.070 < $0.050 -> stop)",
            CodingAgentCacheWarmingFormatter.FormatStatus(new("scheduled", NextWarmAt: now.AddMilliseconds(3720001), Decision: decision), now));
        Assert.Equal("Stopped (extension override, 15% continuation probability, expected savings -$0.070 < $0.050)",
            CodingAgentCacheWarmingFormatter.FormatStatus(new("inactive", Decision: decision, ExtensionOverride: true), now));
        using var usage = System.Text.Json.JsonDocument.Parse("""{"usage":{"cost":{"total":0.100003}},"note":"extension override"}""");
        Assert.Equal("Cache warmed (extension override): $0.100003", CodingAgentCacheWarmingFormatter.FormatUsage(usage.RootElement));
    }
    /// <summary>【CodingAgent】【调度余量】短寿命禁止预热，较长寿命保留十秒或一成余量。</summary>
    /// <param name="ttl">寿命毫秒数。</param><param name="expected">预期间隔。</param>
    [Theory]
    [InlineData(10000, null)] [InlineData(10001, 1d)] [InlineData(30000, 20000d)]
    [InlineData(60000, 50000d)] [InlineData(300000, 270000d)] [InlineData(double.NaN, null)]
    public void Delay_PreservesExpiryMargin(double ttl, double? expected) =>
        Assert.Equal(expected, CodingAgentCacheWarmer.GetCacheWarmingDelayMs(ttl));

    /// <summary>【CodingAgent】【保留策略】显式禁用、请求环境和 Anthropic 思考模式决定是否可预热。</summary>
    [Fact]
    public void RetentionAndReplayability_FollowActualOptions()
    {
        var model = Model() with { Api = "anthropic-messages", PromptCache = new() { Short = 60, Long = 3600 } };
        var options = new SimpleStreamOptions { Env = new Dictionary<string, string> { ["PI_CACHE_RETENTION"] = "long" } };
        Assert.Equal(3600000, CodingAgentCacheWarmer.GetPromptCacheTtlMs(model, options));
        Assert.Equal(60000, CodingAgentCacheWarmer.GetPromptCacheTtlMs(model, options with { CacheRetention = CacheRetention.Short }));
        Assert.Null(CodingAgentCacheWarmer.GetPromptCacheTtlMs(model, options with { CacheRetention = CacheRetention.None }));
        Assert.False(CodingAgentCacheWarmer.IsReplayable(model, options with { Reasoning = ThinkingLevel.High }));
        Assert.True(CodingAgentCacheWarmer.IsReplayable(model with { Compat = new() { ForceAdaptiveThinking = true } }, options with { Reasoning = ThinkingLevel.High }));
        Assert.True(CodingAgentCacheWarmer.IsReplayable(model, options with { Reasoning = ThinkingLevel.Off }));
    }

    /// <summary>【CodingAgent】【重放参数】只覆盖输出上限、重试及取消信号，真实请求取消不牵连预热。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Refresh_ReplaysContextAndPersistsOnlyUsage()
    {
        var clock = new ManualClock();
        using var realSignal = new CancellationTokenSource();
        var model = Model(); var context = new LlmContext { Messages = [new UserMessage("prompt")] };
        var options = new SimpleStreamOptions { CacheRetention = CacheRetention.Short, MaxTokens = 250, MaxRetries = 7,
            Signal = realSignal.Token, SessionId = "original", Headers = new Dictionary<string, string> { ["x-test"] = "kept" } };
        var saved = new List<AssistantMessage>(); var sent = 0;
        await using var warmer = new CodingAgentCacheWarmer((actualModel, actualContext, actualOptions) =>
        {
            sent++; Assert.Same(model, actualModel); Assert.Equal(context, actualContext);
            Assert.Equal(1, actualOptions.MaxTokens); Assert.Equal(0, actualOptions.MaxRetries);
            Assert.Same(options.Headers, actualOptions.Headers); Assert.Equal("original", actualOptions.SessionId);
            Assert.False(actualOptions.Signal.IsCancellationRequested);
            return ValueTask.FromResult(Response());
        }, () => 100000, () => CodingAgentCacheWarmingMode.Idle, (message, note) => { Assert.Null(note); saved.Add(message); }, clock: clock);
        warmer.Start(model, context, options, () => true); realSignal.Cancel();
        Assert.Equal("scheduled", warmer.Status.State);
        clock.Advance(50000);
        Assert.Equal(1, sent); Assert.Single(saved); Assert.Equal(250, options.MaxTokens);
        Assert.Equal("scheduled", warmer.Status.State);
        warmer.OnAgentSettled();
        Assert.Equal(.15m, warmer.Status.Decision!.ContinuationProbability);
        clock.Advance(50000); Assert.Equal(2, sent);
    }

    /// <summary>【CodingAgent】【计费决策】低收益停止；未知用量仍允许扩展显式强制预热并记录来源。</summary>
    /// <param name="force">是否由扩展强制预热。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnknownEconomics_RequiresExtensionOverride(bool force)
    {
        var clock = new ManualClock(); var sent = 0; string? note = null;
        await using var warmer = new CodingAgentCacheWarmer((_, _, _) => { sent++; return ValueTask.FromResult(Response()); },
            () => 0, () => CodingAgentCacheWarmingMode.Streaming, (_, value) => note = value,
            decision => { Assert.Equal("stop", decision.Action); return Task.FromResult(force ? "warm" : decision.Action); }, clock);
        warmer.Start(Model(), new(), new() { CacheRetention = CacheRetention.Short }, () => true);
        Assert.Equal("cache economics unavailable", warmer.Status.Reason);
        clock.Advance(50000);
        Assert.Equal(force ? 1 : 0, sent); Assert.Equal(force ? "extension override" : null, note);
        if (!force) Assert.Equal("cache economics unavailable", warmer.Status.Reason);
    }

    /// <summary>【CodingAgent】【价格阶梯】预热收益包含缓存读取、写入和一枚输出 token 的阶梯价格。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Economics_UsesTiersAndIdleProbability()
    {
        await using var warmer = new CodingAgentCacheWarmer((_, _, _) => throw new InvalidOperationException(),
            () => 100000, () => CodingAgentCacheWarmingMode.Idle, (_, _) => { }, clock: new ManualClock());
        var model = Model() with { Cost = new(1, 1, .1m, 1, [new(20, 8, 2, 25, 50000)]) };
        warmer.Start(model, new(), new() { CacheRetention = CacheRetention.Short }, () => true);
        Assert.Equal(.200008m, warmer.Status.Decision!.WarmCost);
        Assert.Equal(2.3m, warmer.Status.Decision!.MissCost);
        Assert.Equal(2.099992m, warmer.Status.Decision!.ExpectedSavings);
        warmer.OnAgentSettled();
        Assert.Equal(.144992m, warmer.Status.Decision!.ExpectedSavings);
    }

    /// <summary>【CodingAgent】【刷新期限】迟到定时器及延迟扩展回调都不得发送可能已经失效的缓存请求。</summary>
    /// <param name="extensionDelay">是否在扩展决策期间跨越期限。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateRefresh_StopsBeforeSending(bool extensionDelay)
    {
        var clock = new ManualClock(); var calls = 0;
        var decision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var warmer = new CodingAgentCacheWarmer((_, _, _) => { calls++; return ValueTask.FromResult(Response()); },
            () => 100000, () => CodingAgentCacheWarmingMode.Streaming, (_, _) => { },
            _ => decision.Task, clock);
        warmer.Start(Model(), new(), new() { CacheRetention = CacheRetention.Short }, () => true);
        if (extensionDelay) { clock.Advance(50000); Assert.Equal("refreshing", warmer.Status.State); clock.Advance(5001); decision.SetResult("warm"); }
        else clock.Advance(55001);
        await WaitUntilAsync(() => warmer.Status.State == "inactive");
        Assert.Equal("cache refresh deadline missed", warmer.Status.Reason); Assert.Equal(0, calls);
    }

    /// <summary>【CodingAgent】【过时刷新】等待扩展期间切换上下文或启动新请求，使旧决策失效。</summary>
    /// <param name="replace">是否启动另一个请求。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PendingDecision_CannotWarmStaleRequest(bool replace)
    {
        var clock = new ManualClock(); var calls = 0; var current = true;
        var decision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var warmer = new CodingAgentCacheWarmer((_, _, _) => { calls++; return ValueTask.FromResult(Response()); },
            () => 100000, () => CodingAgentCacheWarmingMode.Idle, (_, _) => { }, async _ =>
            { var action = await decision.Task; returned.SetResult(); return action; }, clock);
        warmer.Start(Model(), new(), new() { CacheRetention = CacheRetention.Short }, () => current);
        clock.Advance(50000);
        if (replace) warmer.Start(Model(), new(), new() { CacheRetention = CacheRetention.None }, () => true);
        else current = false;
        decision.SetResult("warm"); await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await warmer.DisposeAsync(); Assert.Equal(0, calls);
    }

    /// <summary>【CodingAgent】【取消请求】关闭模式取消进行中的重放，旧成功响应不能追加用量。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task ModeChange_CancelsInflightRequestAndDiscardsLateUsage()
    {
        var clock = new ManualClock(); var mode = CodingAgentCacheWarmingMode.Idle;
        var response = new AssistantMessageStream(); CancellationToken sentSignal = default; var saved = 0;
        await using var warmer = new CodingAgentCacheWarmer((_, _, options) =>
        { sentSignal = options.Signal; return ValueTask.FromResult(response); }, () => 100000, () => mode, (_, _) => saved++, clock: clock);
        warmer.Start(Model(), new(), new() { CacheRetention = CacheRetention.Short }, () => true);
        clock.Advance(50000); Assert.Equal("refreshing", warmer.Status.State);
        mode = CodingAgentCacheWarmingMode.Off; warmer.OnModeChanged();
        Assert.True(sentSignal.IsCancellationRequested);
        response.Push(new DoneEvent(new AssistantMessage([]) { Usage = new(0, 1) }));
        await warmer.DisposeAsync(); Assert.Equal(0, saved); Assert.Equal("cache warming disabled", warmer.Status.Reason);
    }

    /// <summary>【CodingAgent】【固定期限】成功刷新不得延长原始运行的一小时或空闲半小时限额。</summary>
    /// <param name="idle">是否切入空闲阶段。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SafetyHorizon_DoesNotResetAfterSuccessfulRefresh(bool idle)
    {
        var clock = new ManualClock(); var sent = 0;
        await using var warmer = new CodingAgentCacheWarmer((_, _, _) => { sent++; return ValueTask.FromResult(Response()); },
            () => 100000, () => CodingAgentCacheWarmingMode.Idle, (_, _) => { }, clock: clock);
        warmer.Start(Model(), new(), new() { CacheRetention = CacheRetention.Short }, () => true);
        if (idle) warmer.OnAgentSettled();
        for (var index = 0; index < 80; index++) clock.Advance(50000);
        Assert.Equal(idle ? 36 : 72, sent);
        Assert.Equal(idle ? "30-minute idle safety limit reached" : "one-hour safety limit reached", warmer.Status.Reason);
    }

    /// <summary>【CodingAgent】【故障隔离】扩展异常回退默认决策，失败响应不记费但保留下一次刷新。</summary>
    /// <param name="reason">响应结束状态。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(StopReason.Error)] [InlineData(StopReason.Aborted)]
    public async Task Failure_IsBestEffortAndRemainsScheduled(StopReason reason)
    {
        var clock = new ManualClock(); var calls = 0; var saved = 0;
        await using var warmer = new CodingAgentCacheWarmer((_, _, _) => { calls++; return ValueTask.FromResult(Response(reason)); },
            () => 100000, () => CodingAgentCacheWarmingMode.Streaming, (_, _) => saved++,
            _ => throw new InvalidOperationException("extension fixture"), clock);
        warmer.Start(Model(), new(), new() { CacheRetention = CacheRetention.Short }, () => true);
        clock.Advance(50000); clock.Advance(50000);
        Assert.Equal(2, calls); Assert.Equal(0, saved); Assert.Equal("scheduled", warmer.Status.State);
        warmer.OnAgentSettled(); Assert.Equal("agent run settled", warmer.Status.Reason);
    }

    /// <summary>【CodingAgent】【测试模型】提供预热节省足够的模型和一分钟缓存寿命。</summary>
    /// <returns>离线模型。</returns>
    private static Model Model() => new() { Id = "fixture", Name = "fixture", Provider = "fixture", Api = "fixture",
        Cost = new(10, 3, 1, 12.5m), PromptCache = new() { Short = 60 } };

    /// <summary>【CodingAgent】【测试响应】创建已完成、包含独立计费用量的流。</summary>
    /// <param name="reason">结束状态。</param><returns>模型响应流。</returns>
    private static AssistantMessageStream Response(StopReason reason = StopReason.EndTurn)
    {
        var stream = new AssistantMessageStream();
        stream.Push(new DoneEvent(new AssistantMessage([]) { Provider = "fixture", Model = "fixture", ResponseModel = "billed",
            StopReason = reason, Usage = new(0, 1, CacheReadTokens: 100000, Cost: new(0, .000003m, .1m)) }));
        return stream;
    }

    /// <summary>【CodingAgent】【异步断言】等待异步回调收尾，使用有限超时避免测试挂起。</summary>
    /// <param name="condition">完成条件。</param><returns>等待任务。</returns>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    /// <summary>【CodingAgent】【测试时钟】手动推进墙钟并模拟迟到的一次性定时器。</summary>
    internal sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> _timers = [];
        /// <summary>【CodingAgent】【时钟读取】返回测试时间。</summary><returns>当前时间。</returns>
        public override DateTimeOffset GetUtcNow() => _now;
        /// <summary>【CodingAgent】【时钟推进】在目标时间执行到期回调，便于验证唤醒延迟。</summary>
        /// <param name="milliseconds">推进毫秒数。</param>
        public void Advance(int milliseconds)
        {
            _now = _now.AddMilliseconds(milliseconds);
            foreach (var timer in _timers.Where(timer => !timer.Disposed && timer.Due <= _now).ToArray()) timer.Fire();
        }
        /// <summary>【CodingAgent】【测试调度】注册一次性计时器。</summary>
        /// <param name="callback">回调。</param><param name="state">回调参数。</param><param name="dueTime">延迟。</param>
        /// <param name="period">周期，夹具只允许一次性。</param><returns>计时器。</returns>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new ManualTimer(callback, state, _now.Add(dueTime)); _timers.Add(timer); return timer;
        }
        /// <summary>【CodingAgent】【测试定时器】记录取消状态并执行单次回调。</summary>
        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; } = due;
            public bool Disposed { get; private set; }
            /// <summary>【CodingAgent】【触发计时】执行已到期回调。</summary>
            public void Fire() { Disposed = true; callback(state); }
            /// <summary>【CodingAgent】【修改计时】夹具不支持复用定时器。</summary>
            /// <param name="dueTime">延迟。</param><param name="period">周期。</param><returns>不支持修改。</returns>
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            /// <summary>【CodingAgent】【取消计时】阻止后续触发。</summary>
            public void Dispose() => Disposed = true;
            /// <summary>【CodingAgent】【取消计时】异步接口与同步释放一致。</summary><returns>完成任务。</returns>
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
