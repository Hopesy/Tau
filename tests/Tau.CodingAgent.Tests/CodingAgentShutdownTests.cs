// 作者：xxx
using System.Text.Json;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>RPC 退出请求无需关闭 stdin，命令或模型操作完成后只发一次关闭事件并保存最终状态。</summary>
    /// <param name="duringRun">是否在模型开始时请求退出。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_RpcFinishesCurrentOperationAndExitsWithOpenInput(bool duringRun)
    {
        using var fixture = new Fixture("""
            import fs from 'node:fs';
            export default pi=>{
              pi.registerCommand('bye',{handler:(_,ctx)=>{ctx.shutdown();pi.appendEntry('after-request',{finished:true});}});
              pi.on('agent_start',(_,ctx)=>ctx.shutdown());
              pi.on('session_shutdown',(e,ctx)=>{
                fs.appendFileSync('closed',JSON.stringify({reason:e.reason,entries:ctx.sessionManager.getEntries()})+'\n');
                pi.appendEntry('closing',{reason:e.reason});
              });
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        using var input = new BackgroundLineReader();
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(runner, input, output, extensionCommandStore: commands);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = host.RunAsync(timeout.Token);
        await input.Lines.Writer.WriteAsync(duringRun
            ? """{"id":"p","type":"prompt","message":"finish this answer"}"""
            : """{"id":"p","type":"prompt","message":"/bye"}""", timeout.Token);
        Assert.Equal(0, await running.WaitAsync(timeout.Token));
        Assert.True(runner.IsShutdownRequested);
        Assert.Equal(duringRun ? 1 : 0, provider.Contexts.Count);
        var closed = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "closed"));
        using var saved = JsonDocument.Parse(Assert.Single(closed));
        Assert.Equal("quit", saved.RootElement.GetProperty("reason").GetString());
        Assert.Contains(saved.RootElement.GetProperty("entries").EnumerateArray(), entry => duringRun
            ? entry.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "assistant"
            : entry.TryGetProperty("customType", out var customType) && customType.GetString() == "after-request");
        if (duringRun) Assert.Contains("agent_end", output.ToString());
    }

    /// <summary>交互退出命令阻止后面的输入被发送到模型，并允许关闭钩子收尾。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Shutdown_InteractiveCommandStopsReadingFollowingInput()
    {
        using var fixture = new Fixture("""
            import fs from 'node:fs';
            export default pi=>{
              pi.registerCommand('bye',{handler:(_,ctx)=>ctx.shutdown()});
              pi.on('session_shutdown',e=>fs.appendFileSync('closed',e.reason+'\n'));
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var terminal = new FakeTerminal();
        terminal.QueueInput("/bye");
        terminal.QueueInput("must not be sent");
        terminal.QueueInput("exit");
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, extensionCommandStore: commands);
        Assert.Equal(0, await host.RunAsync());
        Assert.Empty(provider.Contexts);
        Assert.Contains("Goodbye!", terminal.FlattenedText());
        Assert.Equal(["quit"], File.ReadAllLines(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "closed")));
    }

    /// <summary>空闲交互终端通过退出任务唤醒，不需要用户再按一次回车。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Shutdown_InteractiveIdleInputIsCancelled()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var terminal = new ShutdownWaitingTerminal();
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, extensionCommandStore: commands);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = host.RunAsync(timeout.Token);
        await terminal.Entered.Task.WaitAsync(timeout.Token);
        runner.RequestShutdown();
        Assert.Equal(0, await running.WaitAsync(timeout.Token));
        Assert.True(terminal.Cancelled);
        Assert.Empty(provider.Contexts);
    }

    /// <summary>打印模式退出命令不消费剩余提示，也不创建虚假的助手回复。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Shutdown_PrintSkipsRemainingPrompts()
    {
        using var fixture = new Fixture("export default pi=>pi.registerCommand('bye',{handler:(_,ctx)=>ctx.shutdown()});");
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var output = new StringWriter();
        Assert.Equal(0, await new CodingAgentPrintMode(runner, output, new StringWriter()).RunAsync(null, ["/bye", "not sent"]));
        Assert.Empty(provider.Contexts);
        Assert.Empty(output.ToString());
        Assert.True(runner.IsShutdownRequested);
    }

    /// <summary>可取消的终端输入，用于验证空闲退出而不依赖轮询或时间延迟。</summary>
    private sealed class ShutdownWaitingTerminal : ITerminal
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }

        /// <summary>等待宿主取消输入。</summary>
        /// <param name="prompt">提示文字。</param>
        /// <param name="color">提示颜色。</param>
        /// <param name="cancellationToken">宿主的读取取消信号。</param>
        /// <returns>取消前一直等待的输入任务。</returns>
        public async Task<string?> PromptAsync(string prompt, ConsoleColor? color = null, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return null;
        }

        /// <summary>忽略测试中的终端文本。</summary>
        /// <param name="text">输出文字。</param>
        /// <param name="color">输出颜色。</param>
        public void Write(string text, ConsoleColor? color = null) { }

        /// <summary>忽略测试中的终端文本行。</summary>
        /// <param name="text">输出文字。</param>
        /// <param name="color">输出颜色。</param>
        public void WriteLine(string? text = null, ConsoleColor? color = null) { }
    }
}
