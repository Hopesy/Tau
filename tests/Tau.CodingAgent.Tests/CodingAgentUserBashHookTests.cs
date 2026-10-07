// 作者：xxx
using Tau.AgentCore.Harness;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【用户命令接管】按模块和处理器顺序找到第一个结果，保留命令事件字段且不执行后续处理器。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task UserBashHook_FirstDefinedResultWins()
    {
        using var fixture = new Fixture("export default pi=>pi.on('user_bash',()=>undefined);", """
            export default pi=>{
              pi.on('user_bash',(event,ctx)=>({result:{output:event.command+'|'+event.excludeFromContext+'|'+(event.cwd===ctx.cwd),exitCode:7,cancelled:false,truncated:true,fullOutputPath:'saved.txt'}}));
              pi.on('user_bash',()=>{throw Error('must not run');});
            };
            """, "export default pi=>pi.on('user_bash',()=>{throw Error('later module');});");
        var (runner, _) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var hook = await commands.EmitUserBashAsync("echo test", true, runner, CancellationToken.None);
        Assert.NotNull(hook?.Result); Assert.Null(hook.Operations); Assert.Equal("echo test|true|true", hook.Result.Output);
        Assert.Equal(7, hook.Result.ExitCode); Assert.True(hook.Result.Truncated); Assert.Equal("saved.txt", hook.Result.FullOutputPath);
    }

    /// <summary>【CodingAgent】【命令钩子拒绝】不合法结果或异常停止命令，不继续后续处理器，也不自动执行本地命令。</summary>
    /// <param name="body">处理器语句。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("return null")]
    [InlineData("return {}")]
    [InlineData("return {operations:{exec:async()=>({exitCode:0})},result:{output:'x',exitCode:0,cancelled:false,truncated:false}}")]
    [InlineData("return {result:{output:'x',cancelled:false,truncated:false}}")]
    [InlineData("throw Error('rejected command')")]
    public async Task UserBashHook_InvalidResultStopsDispatch(string body)
    {
        using var fixture = new Fixture("export default pi=>{pi.on('user_bash',()=>{" + body + ";});pi.on('user_bash',()=>{throw Error('later handler');});};");
        var (runner, _) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => commands.EmitUserBashAsync("must not execute", false, runner, CancellationToken.None));
        Assert.DoesNotContain("later handler", error.Message); Assert.Empty(runner.Messages.OfType<AgentBashExecutionMessage>());
    }

    /// <summary>【CodingAgent】【扩展命令后端】自定义操作实时回传分片字节，UTF-8 中文不损坏，最终结果通过统一命令管线记录。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task UserBashOperations_StreamsUtf8AndRecordsResult()
    {
        using var fixture = new Fixture("""
            export default pi=>pi.on('user_bash',()=>({operations:{exec:async(command,cwd,{onData,signal})=>{
              if(command!=='remote command'||!cwd||signal.aborted)throw Error('bad context');
              const bytes=Buffer.from('中文😀');for(const byte of bytes)onData(Buffer.from([byte]));return {exitCode:3};
            }}}));
            """);
        var (runner, _) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var hook = await commands.EmitUserBashAsync("remote command", false, runner, CancellationToken.None); Assert.NotNull(hook?.Operations);
        var updates = new List<string>();
        var result = await runner.ExecuteBashAsync("remote command", new HookBashProgress(update => updates.Add(update.Text)), new(Operations: hook.Operations));
        Assert.Equal("中文😀", result.Output); Assert.Equal("中文😀", string.Concat(updates)); Assert.Equal(3, result.ExitCode);
        Assert.Equal(result.Output, Assert.Single(runner.Messages.OfType<AgentBashExecutionMessage>()).Output);
    }

    /// <summary>【CodingAgent】【扩展后端取消】宿主取消送达 Node 的 AbortSignal，保留已收到输出并释放运行状态。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task UserBashOperations_ReceivesCancellation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fixture = new Fixture("""
            export default pi=>pi.on('user_bash',()=>({operations:{exec:async(_,__,{onData,signal})=>{
              onData(Buffer.from('started'));await new Promise(resolve=>signal.addEventListener('abort',resolve,{once:true}));return {exitCode:0};
            }}}));
            """);
        var (runner, _) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var hook = await commands.EmitUserBashAsync("wait", false, runner, deadline.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = runner.ExecuteBashAsync("wait", new HookBashProgress(_ => started.TrySetResult()), new(Operations: hook!.Operations), deadline.Token);
        await started.Task.WaitAsync(deadline.Token); runner.AbortBash(); var result = await running.WaitAsync(deadline.Token);
        Assert.True(result.Cancelled); Assert.Equal("started", result.Output); Assert.False(runner.IsBashRunning); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【后端代次】重载后不能继续调用旧扩展返回的命令后端。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task UserBashOperations_RejectsReloadedCapability()
    {
        using var fixture = new Fixture("export default pi=>pi.on('user_bash',()=>({operations:{exec:async()=>({exitCode:0})}}));");
        var (runner, _) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var hook = await commands.EmitUserBashAsync("old", false, runner, CancellationToken.None);
        fixture.Runtime.Reset();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExecuteBashAsync("old", options: new(Operations: hook!.Operations)));
        Assert.Empty(runner.Messages.OfType<AgentBashExecutionMessage>());
    }

    /// <summary>【CodingAgent】【未消费后端释放】取消展示或切换会话时释放后端，释放后不能再执行。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task UserBashOperations_DisposedCapabilityCannotExecute()
    {
        using var fixture = new Fixture("export default pi=>pi.on('user_bash',()=>({operations:{exec:async()=>({exitCode:0})}}));");
        var (runner, _) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var hook = await commands.EmitUserBashAsync("old", false, runner, CancellationToken.None);
        await hook!.DisposeAsync(); await hook.DisposeAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExecuteBashAsync("old", options: new(Operations: hook.Operations)));
        Assert.Contains("no longer available", error.Message); Assert.Empty(runner.Messages.OfType<AgentBashExecutionMessage>());
    }

    /// <summary>【CodingAgent】【命令进度夹具】立即接收跨进程输出，提供取消测试同步屏障。</summary><param name="receive">输出处理器。</param>
    private sealed class HookBashProgress(Action<CodingAgentShellEvent> receive) : IProgress<CodingAgentShellEvent>
    {
        /// <summary>【CodingAgent】【命令进度】转交输出。</summary><param name="value">输出事件。</param>
        public void Report(CodingAgentShellEvent value) => receive(value);
    }
}
