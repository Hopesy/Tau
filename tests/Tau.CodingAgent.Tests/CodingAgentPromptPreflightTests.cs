// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【认证顺序】缺少认证仍允许 input 扩展执行，但不得执行启动钩子、记录输入或确认 started。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task PromptPreflight_MissingAuthRejectsBeforeStartWithoutRecordingInput()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let inputs=0,starts=0;
              pi.on('input',()=>{inputs++;});
              pi.on('before_agent_start',()=>{starts++;});
              pi.registerCommand('verify',{handler:()=>{if(inputs!==1||starts!==0)throw Error('wrong preflight order');}});
            };
            """) { RequestApiKey = null };
        var (runner, provider) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var dispositions = new List<CodingAgentPromptDisposition>(); var events = new List<AgentEvent>();
        var error = await Assert.ThrowsAsync<ProviderAuthException>(async () =>
        {
            await foreach (var evt in runner.RunWithDispositionAsync([new TextContent("rejected input")], disposition =>
            { dispositions.Add(disposition); return Task.CompletedTask; })) events.Add(evt);
        });
        Assert.Contains("No API key found", error.Message);
        Assert.Empty(dispositions); Assert.Empty(provider.Contexts);
        Assert.Empty(events.OfType<AgentStartEvent>()); Assert.Empty(events.OfType<CodingAgentSettledEvent>());
        Assert.Empty(runner.Messages.OfType<UserMessage>());
        Assert.False(runner.IsStreaming);
        await foreach (var evt in runner.RunAsync("/verify")) Assert.IsNotType<CodingAgentExtensionErrorEvent>(evt);
    }

    /// <summary>【CodingAgent】【本地输入】扩展命令和被 input 接管的提示无需认证，空闲排队也不提前发起模型请求。</summary>
    /// <param name="command">是否走扩展命令入口。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PromptPreflight_HandledAndQueuedInputsDoNotRequireAuth(bool command)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerCommand('local',{handler:()=>{}});
              pi.on('input',event=>event.text==='handled'?{action:'handled'}:undefined);
            };
            """) { RequestApiKey = null };
        var (runner, provider) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var dispositions = new List<CodingAgentPromptDisposition>();
        await foreach (var _ in runner.RunWithDispositionAsync([new TextContent(command ? "/local" : "handled")], disposition =>
        { dispositions.Add(disposition); return Task.CompletedTask; })) { }
        Assert.Equal([CodingAgentPromptDisposition.Handled], dispositions);
        Assert.Equal(CodingAgentQueuedInputDisposition.Queued, runner.SteerWithDisposition(new UserMessage("pending")));
        Assert.Empty(provider.Contexts); Assert.Equal(1, runner.PendingMessageCount);
    }

    /// <summary>【CodingAgent】【RPC 认证拒绝】缺少认证返回失败响应，不创建运行终态，随后仍能查询状态和关闭连接。</summary>
    /// <returns>异步协议回归任务。</returns>
    [Fact]
    public async Task RpcPromptPreflight_MissingAuthReturnsFailureWithoutStartedLifecycle()
    {
        using var fixture = new Fixture("export default pi=>{};") { RequestApiKey = null };
        var (runner, provider) = fixture.CreateRunner();
        using var input = new BackgroundLineReader(); using var output = new BackgroundLineWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = new CodingAgentRpcHost(runner, input, output).RunAsync(deadline.Token);
        input.Lines.Writer.TryWrite("""{"id":"prompt","type":"prompt","message":"rejected"}""");
        var response = await output.WaitAsync(_ => true, deadline.Token);
        Assert.Equal("response", response.GetProperty("type").GetString());
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Contains("No API key found", response.GetProperty("error").GetString());
        input.Lines.Writer.TryWrite("""{"id":"state","type":"get_state"}""");
        var state = await output.WaitAsync(_ => true, deadline.Token);
        Assert.Equal("state", state.GetProperty("id").GetString());
        Assert.False(state.GetProperty("data").GetProperty("isStreaming").GetBoolean());
        Assert.Empty(provider.Contexts);
        input.Lines.Writer.TryComplete(); Assert.Equal(0, await running.WaitAsync(deadline.Token));
    }
}
