using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentCountdownTimerTests
{
    [Fact]
    public void Constructor_ImmediatelyReportsCeiledSeconds()
    {
        var ticks = new List<int>();

        using var timer = new CodingAgentCountdownTimer(
            TimeSpan.FromMilliseconds(1500),
            ticks.Add,
            static () => throw new InvalidOperationException("Should not expire during construction."),
            tickInterval: TimeSpan.FromDays(1));

        Assert.Equal([2], ticks);
        Assert.Equal(2, timer.RemainingSeconds);
    }

    [Fact]
    public void Tick_DecrementsRequestsRenderAndExpiresAtZero()
    {
        var ticks = new List<int>();
        var renderCount = 0;
        var expireCount = 0;
        using var timer = new CodingAgentCountdownTimer(
            TimeSpan.FromMilliseconds(2100),
            ticks.Add,
            () => expireCount++,
            () => renderCount++,
            TimeSpan.FromDays(1));

        timer.Tick();
        timer.Tick();
        timer.Tick();

        Assert.Equal([3, 2, 1, 0], ticks);
        Assert.Equal(3, renderCount);
        Assert.Equal(1, expireCount);
        Assert.Equal(0, timer.RemainingSeconds);
    }

    [Fact]
    public void Dispose_PreventsFurtherTicksAndExpire()
    {
        var ticks = new List<int>();
        var renderCount = 0;
        var expireCount = 0;
        var timer = new CodingAgentCountdownTimer(
            TimeSpan.FromMilliseconds(1000),
            ticks.Add,
            () => expireCount++,
            () => renderCount++,
            TimeSpan.FromDays(1));

        timer.Dispose();
        timer.Tick();

        Assert.Equal([1], ticks);
        Assert.Equal(0, renderCount);
        Assert.Equal(0, expireCount);
    }
}
