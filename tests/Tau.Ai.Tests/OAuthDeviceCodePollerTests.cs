// 作者：xxx
using Tau.Ai.Auth.OAuth;

namespace Tau.Ai.Tests;

/// <summary>【AI】【设备码轮询测试】使用虚拟时间验证期限与限速，不产生真实延迟或外网请求。</summary>
public sealed class OAuthDeviceCodePollerTests
{
    /// <summary>【AI】【设备码轮询测试】默认五秒，最小一秒，并按要求延迟首轮。</summary>
    /// <param name="interval">请求间隔。</param><param name="waitFirst">首次等待。</param><param name="expected">预期间隔。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(null, false, 5000)]
    [InlineData(null, true, 5000)]
    [InlineData(0.1, false, 1000)]
    [InlineData(-1.0, true, 1000)]
    [InlineData(2.3456, true, 2345)]
    public async Task Polling_UsesExpectedIntervals(double? interval, bool waitFirst, double expected)
    {
        var time = new VirtualClock(); var calls = 0;
        var result = await OAuthDeviceCodePoller.PollAsync(() => Task.FromResult(++calls == 1
            ? new OAuthDeviceCodePollResult<string>("pending") : new("complete", "done")), default, interval, 60, waitFirst, time.Clock);
        Assert.Equal("done", result);
        Assert.Equal(waitFirst ? new[] { expected, expected } : [expected], time.Waits);
    }

    /// <summary>【AI】【设备码轮询测试】服务端间隔优先，缺少或无效间隔才累计五秒。</summary>
    /// <param name="suggested">建议秒数。</param><param name="expected">预期毫秒数。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(null, 10000)]
    [InlineData(2.0, 2000)]
    [InlineData(0.2, 1000)]
    [InlineData(0.0, 10000)]
    [InlineData(-2.0, 10000)]
    [InlineData(double.NaN, 10000)]
    [InlineData(double.PositiveInfinity, 10000)]
    public async Task SlowDown_UsesServerIntervalOrAddsFiveSeconds(double? suggested, double expected)
    {
        var time = new VirtualClock(); var calls = 0;
        await OAuthDeviceCodePoller.PollAsync(() => Task.FromResult(++calls == 1
            ? new OAuthDeviceCodePollResult<string>("slow_down", IntervalSeconds: suggested) : new("complete", "done")), default, clock: time.Clock);
        Assert.Equal(expected, Assert.Single(time.Waits));
    }

    /// <summary>【AI】【设备码轮询测试】有效期不足一个间隔时，只等待剩余时间且不多发请求。</summary>
    /// <param name="waitFirst">是否首轮等待。</param><param name="slowDown">是否收到限速。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Deadline_BoundsWaitsAndReportsClockDrift(bool waitFirst, bool slowDown)
    {
        var time = new VirtualClock(); var calls = 0;
        var error = await Assert.ThrowsAsync<TimeoutException>(() => OAuthDeviceCodePoller.PollAsync(() =>
        { calls++; return Task.FromResult(new OAuthDeviceCodePollResult<string>(slowDown ? "slow_down" : "pending")); }, default, expiresInSeconds: 0.5, waitBeforeFirstPoll: waitFirst, clock: time.Clock));
        Assert.Equal(500, Assert.Single(time.Waits));
        Assert.Equal(waitFirst ? 0 : 1, calls);
        Assert.Equal(slowDown ? OAuthDeviceCodePoller.SlowDownTimeoutMessage : "Device flow timed out", error.Message);
    }

    /// <summary>【AI】【设备码轮询测试】失败结果直接传递，完成结果不额外等待。</summary>
    /// <param name="failed">是否失败。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalResult_StopsImmediately(bool failed)
    {
        var time = new VirtualClock();
        var pending = OAuthDeviceCodePoller.PollAsync(() => Task.FromResult(new OAuthDeviceCodePollResult<string>(failed ? "failed" : "complete", "value", "denied")), default, clock: time.Clock);
        if (failed) Assert.Equal("denied", (await Assert.ThrowsAsync<InvalidOperationException>(() => pending)).Message);
        else Assert.Equal("value", await pending);
        Assert.Empty(time.Waits);
    }

    /// <summary>【AI】【设备码轮询测试】轮询或等待期间取消会保留取消语义。</summary>
    /// <param name="duringWait">是否等待期间取消。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_StopsPendingFlow(bool duringWait)
    {
        using var source = new CancellationTokenSource(); var calls = 0;
        var clock = new OAuthFlowClock { SleepAsync = (_, token) => { source.Cancel(); return Task.Delay(Timeout.Infinite, token); } };
        if (!duringWait) source.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => OAuthDeviceCodePoller.PollAsync(() =>
        { calls++; return Task.FromResult(new OAuthDeviceCodePollResult<string>("pending")); }, source.Token, clock: clock));
        Assert.Equal("Login cancelled", error.Message);
        Assert.Equal(duringWait ? 1 : 0, calls);
    }

    /// <summary>【AI】【设备码轮询测试】已过期设备码不触发首次请求。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ExpiredCode_DoesNotPoll()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => OAuthDeviceCodePoller.PollAsync<string>(() => throw new InvalidOperationException("Unexpected poll"), default, expiresInSeconds: 0, clock: new VirtualClock().Clock));
    }

    /// <summary>【AI】【设备码虚拟时钟】按等待时间推进并记录所有间隔。</summary>
    private sealed class VirtualClock
    {
        private double _now;
        public List<double> Waits { get; } = [];
        public OAuthFlowClock Clock { get; }
        /// <summary>绑定当前时间与立即完成的可取消等待。</summary>
        public VirtualClock() => Clock = new OAuthFlowClock { NowMilliseconds = () => _now, SleepAsync = SleepAsync };
        /// <summary>记录等待并推进虚拟时间。</summary><param name="milliseconds">毫秒数。</param><param name="token">信号。</param><returns>完成任务。</returns>
        private Task SleepAsync(double milliseconds, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Waits.Add(milliseconds); _now += milliseconds; return Task.CompletedTask; }
    }
}
