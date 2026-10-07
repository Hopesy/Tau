// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【完整清空】用户清空操作与逐回合消费模式无关，并保留原始文本及纯图片占位。</summary>
    /// <param name="steeringMode">引导队列模式。</param><param name="followUpMode">跟进队列模式。</param>
    [Theory]
    [InlineData(AgentQueueMode.OneAtATime, AgentQueueMode.OneAtATime)]
    [InlineData(AgentQueueMode.OneAtATime, AgentQueueMode.All)]
    [InlineData(AgentQueueMode.All, AgentQueueMode.OneAtATime)]
    [InlineData(AgentQueueMode.All, AgentQueueMode.All)]
    public void ClearQueue_DrainsEveryMessageRegardlessOfDeliveryMode(AgentQueueMode steeringMode, AgentQueueMode followUpMode)
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, _) = fixture.CreateRunner(); runner.SteeringMode = steeringMode; runner.FollowUpMode = followUpMode;
        runner.Steer("  first\nline  "); runner.Steer("second"); runner.Steer(new UserMessage([new ImageContent("YWJj", "image/png")]));
        runner.FollowUp("  follow  "); runner.FollowUp("last"); runner.FollowUp(new UserMessage([new ImageContent("YWJj", "image/png")]));
        Assert.Equal(6, runner.PendingMessageCount);
        var cleared = runner.DrainQueuedMessages();
        Assert.Equal(["  first\nline  ", "second", ""], cleared.Steering);
        Assert.Equal(["  follow  ", "last", ""], cleared.FollowUp);
        Assert.Equal(0, runner.PendingMessageCount);
        Assert.Empty(runner.DrainQueuedMessages().Steering);
        Assert.Empty(runner.DrainQueuedMessages().FollowUp);
    }

    /// <summary>【CodingAgent】【RPC 清空】默认单条模式下多次空闲排队后，一次 clear_queue 返回并移除全部条目。</summary>
    /// <returns>异步协议回归。</returns>
    [Fact]
    public async Task RpcClearQueue_ReturnsAllPendingInputs()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunner();
        using var input = new BackgroundLineReader(); using var output = new BackgroundLineWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = new CodingAgentRpcHost(runner, input, output).RunAsync(deadline.Token);
        for (var index = 0; index < 3; index++)
        {
            input.Lines.Writer.TryWrite(new System.Text.Json.Nodes.JsonObject { ["type"] = "steer", ["message"] = "s" + index }.ToJsonString());
            input.Lines.Writer.TryWrite(new System.Text.Json.Nodes.JsonObject { ["type"] = "follow_up", ["message"] = "f" + index }.ToJsonString());
        }
        input.Lines.Writer.TryWrite("""{"id":"clear","type":"clear_queue"}""");
        var result = await output.WaitAsync(item => item.TryGetProperty("id", out var id) && id.GetString() == "clear", deadline.Token);
        Assert.Equal(["s0", "s1", "s2"], result.GetProperty("data").GetProperty("steering").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["f0", "f1", "f2"], result.GetProperty("data").GetProperty("followUp").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(0, runner.PendingMessageCount); Assert.Empty(provider.Contexts);
        input.Lines.Writer.TryComplete(); Assert.Equal(0, await running.WaitAsync(deadline.Token));
    }
}
