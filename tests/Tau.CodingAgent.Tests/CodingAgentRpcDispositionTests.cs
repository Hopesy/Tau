// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【接收瞬间取消】确认 started 的回调取消运行时仍发送一次结算，不遗留等待。</summary>
    /// <returns>异步联调任务。</returns>
    [Fact]
    public async Task PromptDisposition_CancelDuringAcceptanceStillSettles()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, _) = fixture.CreateRunner(); var events = new List<AgentEvent>();
        var dispositions = new List<CodingAgentPromptDisposition>();
        try
        {
            await foreach (var evt in runner.RunWithDispositionAsync([new TextContent("input")], disposition =>
            { dispositions.Add(disposition); runner.Abort(); return Task.CompletedTask; })) events.Add(evt);
        }
        catch (OperationCanceledException) { }
        Assert.Equal([CodingAgentPromptDisposition.Started], dispositions);
        Assert.Single(events.OfType<CodingAgentSettledEvent>());
        await runner.WaitForIdleAsync(default);
    }
    /// <summary>【CodingAgent】【RPC 原生接收】运行或空闲时区分扩展接管、排队和新运行，保留正确 input 来源。</summary>
    /// <param name="command">输入接口。</param><param name="active">是否已有运行。</param><param name="handled">是否由扩展接管。</param>
    /// <returns>异步联调任务。</returns>
    [Theory]
    [InlineData("prompt", false, false)] [InlineData("prompt", false, true)]
    [InlineData("prompt", true, false)] [InlineData("prompt", true, true)]
    [InlineData("steer", false, false)] [InlineData("steer", false, true)]
    [InlineData("steer", true, false)] [InlineData("steer", true, true)]
    [InlineData("follow_up", false, false)] [InlineData("follow_up", false, true)]
    [InlineData("follow_up", true, false)] [InlineData("follow_up", true, true)]
    public async Task RpcInput_ReturnsNativeDisposition(string command, bool active, bool handled)
    {
        using var fixture = new Fixture("""
            export default pi=>pi.on('input',event=>{
              if(event.source!=='rpc')throw Error('wrong input source');
              if(event.text!=='hold' && event.streamingBehavior!==__BEHAVIOR__)
                return event.text==='handled'?{action:'transform',text:'wrong mode'}:{action:'handled'};
              if(event.text==='handled')return {action:'handled'};
            });
            """.Replace("__BEHAVIOR__", !active || command == "prompt" && handled ? "undefined" : command == "steer" ? "'steer'" : "'followUp'", StringComparison.Ordinal));
        var (runner, provider) = fixture.CreateRunner();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StreamFactory = options =>
        {
            var stream = new AssistantMessageStream();
            options.Signal.Register(() => stream.Push(new DoneEvent(new AssistantMessage([]) { StopReason = StopReason.Aborted })));
            started.TrySetResult(); return stream;
        };
        using var input = new BackgroundLineReader(); using var output = new BackgroundLineWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var host = new CodingAgentRpcHost(runner, input, output);
        var running = host.RunAsync(deadline.Token);
        if (active)
        {
            input.Lines.Writer.TryWrite("""{"id":"initial","type":"prompt","message":"hold"}""");
            await started.Task.WaitAsync(deadline.Token);
        }
        input.Lines.Writer.TryWrite(new System.Text.Json.Nodes.JsonObject { ["id"] = "test", ["type"] = command,
            ["message"] = handled ? "handled" : "ordinary", ["streamingBehavior"] = command == "prompt" && active && !handled ? "followUp" : null }.ToJsonString());
        var response = await output.WaitAsync(item => item.TryGetProperty("id", out var id) && id.GetString() == "test", deadline.Token);
        Assert.True(response.GetProperty("success").GetBoolean());
        Assert.Equal(handled ? "handled" : command == "prompt" && !active ? "started" : "queued",
            response.GetProperty("data").GetProperty("disposition").GetString());
        if (active || command == "prompt" && !handled)
        {
            await started.Task.WaitAsync(deadline.Token);
            input.Lines.Writer.TryWrite("""{"id":"abort","type":"abort"}""");
            await output.WaitAsync(item => item.GetProperty("type").GetString() == "agent_settled", deadline.Token);
        }
        else Assert.Empty(provider.Contexts);
        input.Lines.Writer.TryComplete(); Assert.Equal(0, await running.WaitAsync(deadline.Token));
    }

    /// <summary>【CodingAgent】【命令接收】命令自己的主动模型运行不改变原命令提示已经被处理的结果。</summary>
    /// <param name="trigger">命令是否发送新用户消息。</param><returns>异步联调任务。</returns>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PromptDisposition_ExtensionCommandIsHandledEvenWhenItSendsInput(bool trigger)
    {
        using var fixture = new Fixture("""
            export default pi=>pi.registerCommand('custom',{handler:()=>{__BODY__}});
            """.Replace("__BODY__", trigger ? "pi.sendUserMessage('new input');" : "", StringComparison.Ordinal));
        var (runner, provider) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var dispositions = new List<CodingAgentPromptDisposition>(); var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunWithDispositionAsync([new TextContent("/custom")], disposition =>
        { dispositions.Add(disposition); return Task.CompletedTask; })) events.Add(evt);
        Assert.Equal([CodingAgentPromptDisposition.Handled], dispositions);
        Assert.Equal(trigger ? 1 : 0, provider.Contexts.Count);
        Assert.Equal(trigger ? 1 : 0, events.OfType<CodingAgentSettledEvent>().Count());
    }
}
