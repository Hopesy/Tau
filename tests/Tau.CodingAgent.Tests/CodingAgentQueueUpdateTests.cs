// 作者：xxx
using System.Collections.Concurrent;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【队列事件顺序】每个 message_start 之前逐条扣减，批量模式也保留尚未开始的输入。</summary>
    /// <param name="mode">底层调度模式。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(AgentQueueMode.OneAtATime)] [InlineData(AgentQueueMode.All)]
    public async Task QueueUpdates_PrecedeEachUserMessageStart(AgentQueueMode mode)
    {
        using var fixture = new Fixture("export default pi=>{};"); var (runner, _) = fixture.CreateRunner();
        runner.SteeringMode = mode; runner.FollowUpMode = mode;
        var updates = new ConcurrentQueue<CodingAgentQueueUpdateEvent>();
        runner.BackgroundEvent += (evt, _) => { if (evt is CodingAgentQueueUpdateEvent update) updates.Enqueue(update); return Task.CompletedTask; };
        runner.Steer("first"); runner.Steer("second"); runner.FollowUp("follow");
        await runner.WaitForQueueNotificationsAsync();
        Assert.Equal([1, 2, 3], updates.Select(update => update.Steering.Count + update.FollowUp.Count));
        var snapshot = runner.GetSteeringMessages(); Assert.Equal(["first", "second"], snapshot);
        Assert.IsType<string[]>(snapshot)[0] = "mutated";
        Assert.Equal(["first", "second"], runner.GetSteeringMessages());
        var started = new List<string>();
        await foreach (var evt in runner.RunAsync("initial"))
        {
            if (evt is not MessageStartEvent { Message: UserMessage user }) continue;
            var text = string.Concat(user.Content.OfType<TextContent>().Select(part => part.Text));
            if (text == "initial") continue;
            started.Add(text);
            Assert.Equal(3 - started.Count, runner.PendingMessageCount);
            var latest = updates.Last();
            Assert.Equal(runner.GetSteeringMessages(), latest.Steering);
            Assert.Equal(runner.GetFollowUpMessages(), latest.FollowUp);
        }
        Assert.Equal(["first", "second", "follow"], started);
        Assert.Equal([1, 2, 3, 2, 1, 0], updates.Select(update => update.Steering.Count + update.FollowUp.Count));
        Assert.Equal(["first"], updates.First().Steering);
    }

    /// <summary>【CodingAgent】【队列身份】纯图片与同文本输入按真实消息逐条消费，清空和重置发布空快照。</summary>
    /// <param name="reset">是否通过重置清空。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task QueueUpdates_PreserveImagesDuplicatesAndClear(bool reset)
    {
        using var fixture = new Fixture("export default pi=>{};"); var (runner, _) = fixture.CreateRunner();
        var updates = new ConcurrentQueue<CodingAgentQueueUpdateEvent>();
        runner.BackgroundEvent += (evt, _) => { if (evt is CodingAgentQueueUpdateEvent update) updates.Enqueue(update); return Task.CompletedTask; };
        runner.Steer("same"); runner.FollowUp("same");
        runner.Steer(new UserMessage([new ImageContent("YWJj", "image/png")]));
        await foreach (var _ in runner.RunAsync("initial")) { }
        Assert.Equal(0, runner.PendingMessageCount);
        Assert.Empty(runner.GetSteeringMessages()); Assert.Empty(runner.GetFollowUpMessages());
        runner.Steer("pending"); runner.FollowUp("last");
        if (reset) runner.ResetSession(); else runner.DrainQueuedMessages();
        await runner.WaitForQueueNotificationsAsync();
        Assert.Empty(updates.Last().Steering); Assert.Empty(updates.Last().FollowUp);
        Assert.Equal(0, runner.PendingMessageCount);
        Assert.Contains(updates, update => update.Steering.Contains(""));
    }

    /// <summary>【CodingAgent】【自定义队列】自定义上下文消息参与运行队列，但不伪装为可恢复的用户输入。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task QueueUpdates_ExcludeCustomContextMessages()
    {
        using var fixture = new Fixture("export default pi=>{};"); var (runner, _) = fixture.CreateRunner();
        var updates = new List<CodingAgentQueueUpdateEvent>();
        runner.BackgroundEvent += (evt, _) => { if (evt is CodingAgentQueueUpdateEvent update) updates.Add(update); return Task.CompletedTask; };
        runner.Steer(new AgentCustomMessage("note", [new TextContent("hidden context")], false));
        Assert.Equal(0, runner.PendingMessageCount); Assert.True(runner.HasBoundaryQueuedMessages);
        var cleared = runner.DrainQueuedMessages(); await runner.WaitForQueueNotificationsAsync();
        Assert.Empty(cleared.Steering); Assert.Empty(cleared.FollowUp); Assert.False(runner.HasBoundaryQueuedMessages);
        Assert.Empty(Assert.Single(updates).Steering);
    }

    /// <summary>【CodingAgent】【会话清空通知】通知处理积压时切换会话，最后的空队列仍须送达宿主。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task QueueUpdates_SessionReplacementPreservesClearNotification()
    {
        using var fixture = new Fixture("export default pi=>{};"); var (runner, _) = fixture.CreateRunner();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new ConcurrentQueue<CodingAgentQueueUpdateEvent>();
        runner.BackgroundEvent += async (evt, _) =>
        {
            if (evt is not CodingAgentQueueUpdateEvent update) return;
            updates.Enqueue(update);
            if (updates.Count == 1) { entered.TrySetResult(); await release.Task; }
        };
        try
        {
            runner.Steer("old session");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var oldId = runner.SessionId;
            Assert.False((await runner.NewSessionAsync()).Cancelled);
            Assert.NotEqual(oldId, runner.SessionId);
        }
        finally { release.TrySetResult(); }
        await runner.WaitForQueueNotificationsAsync();
        Assert.Equal(2, updates.Count);
        Assert.Empty(updates.Last().Steering); Assert.Empty(updates.Last().FollowUp);
    }

    /// <summary>【CodingAgent】【RPC 队列】队列事件先于命令确认，内存会话的 get_state 包含真实会话标识。</summary>
    /// <returns>异步协议回归。</returns>
    [Fact]
    public async Task RpcQueueUpdates_PrecedeAcknowledgementAndExposeMemorySessionId()
    {
        using var fixture = new Fixture("export default pi=>{};"); var (runner, _) = fixture.CreateRunner();
        using var input = new BackgroundLineReader(); using var output = new BackgroundLineWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = new CodingAgentRpcHost(runner, input, output).RunAsync(deadline.Token);
        input.Lines.Writer.TryWrite("""{"id":"steer","type":"steer","message":"pending"}""");
        var first = await output.WaitAsync(_ => true, deadline.Token);
        Assert.Equal("queue_update", first.GetProperty("type").GetString());
        Assert.Equal("pending", Assert.Single(first.GetProperty("steering").EnumerateArray()).GetString());
        var accepted = await output.WaitAsync(_ => true, deadline.Token); Assert.Equal("steer", accepted.GetProperty("id").GetString());
        input.Lines.Writer.TryWrite("""{"id":"state","type":"get_state"}""");
        var state = await output.WaitAsync(item => item.TryGetProperty("id", out var id) && id.GetString() == "state", deadline.Token);
        Assert.Equal(runner.SessionId, state.GetProperty("data").GetProperty("sessionId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(runner.SessionId));
        Assert.Equal(1, state.GetProperty("data").GetProperty("pendingMessageCount").GetInt32());
        input.Lines.Writer.TryWrite("""{"id":"clear","type":"clear_queue"}""");
        var cleared = await output.WaitAsync(_ => true, deadline.Token);
        Assert.Equal("queue_update", cleared.GetProperty("type").GetString());
        Assert.Empty(cleared.GetProperty("steering").EnumerateArray());
        var response = await output.WaitAsync(_ => true, deadline.Token); Assert.Equal("clear", response.GetProperty("id").GetString());
        input.Lines.Writer.TryComplete(); Assert.Equal(0, await running.WaitAsync(deadline.Token));
    }
}
