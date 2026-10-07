// 作者：xxx
using Tau.AgentCore.Harness;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentCompositionTurnInputTests
{
    /// <summary>【CodingAgent】【宿主命令钩子】完整交互入口遵守扩展结果、远程执行、本地放行和错误拒绝四种结果。</summary>
    /// <param name="mode">处理器行为。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("result")]
    [InlineData("operations")]
    [InlineData("local")]
    [InlineData("reject")]
    public async Task Host_UserBashHookControlsExecution(string mode)
    {
        using var fixture = new StreamingSessionFixture(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var provider = new InterruptProvider(); var runner = fixture.CreateRunner(provider);
        var file = Path.Combine(runner.WorkingDirectory, "user-bash.js");
        var body = mode switch
        {
            "result" => "return {result:{output:'hook output',exitCode:4,cancelled:false,truncated:true,fullOutputPath:'saved.txt'}};",
            "operations" => "return {operations:{exec:async(command,cwd,{onData})=>{onData(Buffer.from('remote output'));return {exitCode:5};}}};",
            "local" => "return undefined;",
            _ => "throw Error('blocked by hook');"
        };
        File.WriteAllText(file, "export default pi=>pi.on('user_bash',(event)=>{if(event.command!=='command'||!event.excludeFromContext||'_tauOperationId' in event)throw Error('bad event');" + body + "});");
        using var commands = new CodingAgentExtensionCommandStore(runner.WorkingDirectory, explicitPaths: [file], includeDefaults: false);
        var main = new Events(); var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)), bindings: Native());
        var ui = new InteractiveConsoleSession(new FakeTerminal(), editor); var localCalls = 0;
        var host = new CodingAgentHost(ui, runner, extensionCommandStore: commands, initialMessages: ["!!command"])
        {
            EditorKeyTimeProvider = new StreamingFixedClock(),
            DirectBashOperations = new BashTestOperations(async (_, output, _) => { localCalls++; await output("local output"); return 0; })
        };
        var running = host.RunAsync(deadline.Token);
        await WaitForBashConditionAsync(() => mode == "reject"
            ? ui.Transcript.Any(entry => entry.Text.Contains("blocked by hook"))
            : !runner.IsBashRunning && runner.Messages.OfType<AgentBashExecutionMessage>().Any(), deadline.Token);
        main.Key(ConsoleKey.C, ConsoleModifiers.Control); main.Key(ConsoleKey.C, ConsoleModifiers.Control); await running;
        Assert.Equal(mode == "local" ? 1 : 0, localCalls); Assert.Equal(0, provider.Calls);
        if (mode == "reject") Assert.Empty(runner.Messages.OfType<AgentBashExecutionMessage>());
        else
        {
            var message = Assert.Single(runner.Messages.OfType<AgentBashExecutionMessage>()); Assert.True(message.ExcludeFromContext);
            Assert.Equal(mode switch { "result" => "hook output", "operations" => "remote output", _ => "local output" }, message.Output);
            Assert.Equal(mode switch { "result" => 4, "operations" => 5, _ => 0 }, message.ExitCode);
            Assert.Equal(mode == "result", message.Truncated); Assert.Equal(mode == "result" ? "saved.txt" : null, message.FullOutputPath);
        }
        Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【直接命令入口】! 和 !! 使用 Shell 后端，流式展示输出并保存原生记录，不发起模型请求。</summary>
    /// <param name="excluded">是否使用 !!。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_DirectBashInputExecutesWithoutModel(bool excluded)
    {
        using var fixture = new StreamingSessionFixture(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var provider = new InterruptProvider(); var runner = fixture.CreateRunner(provider);
        var main = new Events(); main.Key(ConsoleKey.Enter);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)), bindings: Native());
        editor.SetExpandedDraft((excluded ? "!!" : "!") + " command");
        var ui = new InteractiveConsoleSession(new FakeTerminal(), editor); var calls = 0;
        var host = new CodingAgentHost(ui, runner)
        {
            EditorKeyTimeProvider = new StreamingFixedClock(),
            DirectBashOperations = new BashTestOperations(async (context, output, _) =>
            { calls++; Assert.Equal("command", context.Command); await output("first\n"); await output("second"); return 0; })
        };
        var running = host.RunAsync(deadline.Token);
        await WaitForBashConditionAsync(() => runner.Messages.OfType<AgentBashExecutionMessage>().Any(), deadline.Token);
        main.Key(ConsoleKey.C, ConsoleModifiers.Control); main.Key(ConsoleKey.C, ConsoleModifiers.Control); await running;
        var message = Assert.Single(runner.Messages.OfType<AgentBashExecutionMessage>());
        Assert.Equal(1, calls); Assert.Equal(0, provider.Calls); Assert.Equal(excluded, message.ExcludeFromContext);
        Assert.Equal("first\nsecond", message.Output); Assert.Contains(ui.Transcript, entry => entry.Text.Contains("second"));
        Assert.Empty(runner.Messages.OfType<Tau.Ai.UserMessage>()); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【直接命令 Escape】运行时输入草稿后按 Escape，只取消命令并保留草稿。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Host_EscapeCancelsDirectBashAndKeepsDraft()
    {
        using var fixture = new StreamingSessionFixture(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = fixture.CreateRunner(new InterruptProvider()); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var main = new Events(); var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)), bindings: Native());
        var ui = new InteractiveConsoleSession(new FakeTerminal(), editor);
        var host = new CodingAgentHost(ui, runner, initialMessages: ["!wait"])
        {
            EditorKeyTimeProvider = new StreamingFixedClock(),
            DirectBashOperations = new BashTestOperations(async (_, output, token) =>
            { await output("partial"); entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0; })
        };
        var running = host.RunAsync(deadline.Token); await entered.Task.WaitAsync(deadline.Token);
        main.Text("draft"); main.Key(ConsoleKey.Escape);
        await WaitForBashConditionAsync(() => runner.Messages.OfType<AgentBashExecutionMessage>().Any(), deadline.Token);
        Assert.Equal("draft", ui.GetDraft()); Assert.True(Assert.Single(runner.Messages.OfType<AgentBashExecutionMessage>()).Cancelled);
        main.Key(ConsoleKey.C, ConsoleModifiers.Control); main.Key(ConsoleKey.C, ConsoleModifiers.Control); await running;
        Assert.False(runner.IsBashRunning); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【重复命令保护】直接命令未结束时恢复第二次输入，不能并发执行或把命令发给模型。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Host_BusyBashRestoresSubmittedCommand()
    {
        using var fixture = new StreamingSessionFixture(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var provider = new InterruptProvider(); var runner = fixture.CreateRunner(provider); var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var main = new Events(); var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)), bindings: Native());
        var ui = new InteractiveConsoleSession(new FakeTerminal(), editor);
        var host = new CodingAgentHost(ui, runner, initialMessages: ["!first"])
        {
            EditorKeyTimeProvider = new StreamingFixedClock(),
            DirectBashOperations = new BashTestOperations(async (_, _, token) =>
            { calls++; entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0; })
        };
        var running = host.RunAsync(deadline.Token); await entered.Task.WaitAsync(deadline.Token);
        main.Text("!!second"); main.Key(ConsoleKey.Enter);
        await WaitForBashConditionAsync(() => ui.GetDraft() == "!!second" && ui.Transcript.Any(entry => entry.Text.Contains("already running")), deadline.Token);
        Assert.Equal("!!second", ui.GetDraft()); Assert.Equal(1, calls); Assert.Equal(0, provider.Calls);
        main.Key(ConsoleKey.Escape);
        await WaitForBashConditionAsync(() => !runner.IsBashRunning, deadline.Token);
        main.Key(ConsoleKey.C, ConsoleModifiers.Control); main.Key(ConsoleKey.C, ConsoleModifiers.Control); await running;
        Assert.Single(runner.Messages.OfType<AgentBashExecutionMessage>()); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【生成中直接命令】命令输出先显示而结果延后写入，Escape 仍优先中断模型。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Host_StreamingBashDoesNotBecomeSteeringInput()
    {
        using var fixture = new StreamingSessionFixture(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var provider = new InterruptProvider(); var runner = fixture.CreateRunner(provider);
        var stream = new Events { Gate = provider.Started.Task }; stream.Text("! command"); stream.Key(ConsoleKey.Enter);
        var main = new Events(); var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)), bindings: Native());
        var host = new CodingAgentHost(new(new FakeTerminal(), editor), runner, initialMessages: ["start"], turnInputSource: Create(stream, Native()))
        {
            EditorKeyTimeProvider = new StreamingFixedClock(),
            DirectBashOperations = new BashTestOperations(async (_, output, _) => { await output("side output"); return 0; })
        };
        var running = host.RunAsync(deadline.Token);
        await WaitForBashConditionAsync(() => runner.HasPendingBashMessages, deadline.Token);
        Assert.True(runner.IsStreaming); Assert.Empty(runner.Messages.OfType<AgentBashExecutionMessage>()); Assert.Equal(0, runner.PendingMessageCount);
        stream.Key(ConsoleKey.Escape); await main.Waiting.Task.WaitAsync(deadline.Token);
        Assert.Equal("side output", Assert.Single(runner.Messages.OfType<AgentBashExecutionMessage>()).Output);
        Assert.True(provider.Cancelled); Assert.Equal(1, provider.Calls);
        main.Key(ConsoleKey.C, ConsoleModifiers.Control); main.Key(ConsoleKey.C, ConsoleModifiers.Control); await running;
        Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【命令退出收尾】宿主退出会等待自己启动的直接命令取消并保存已收到的输出。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Host_ExitWaitsForBashCancellation()
    {
        using var fixture = new StreamingSessionFixture(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = fixture.CreateRunner(new InterruptProvider()); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var main = new Events(); var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)), bindings: Native());
        var host = new CodingAgentHost(new(new FakeTerminal(), editor), runner, initialMessages: ["!wait"])
        {
            DirectBashOperations = new BashTestOperations(async (_, output, token) =>
            { await output("before exit"); entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0; })
        };
        var running = host.RunAsync(deadline.Token); await entered.Task.WaitAsync(deadline.Token); main.Key(ConsoleKey.D, ConsoleModifiers.Control); await running;
        Assert.False(runner.IsBashRunning); Assert.True(Assert.Single(runner.Messages.OfType<AgentBashExecutionMessage>()).Cancelled);
        Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【异步命令屏障】等待后台命令达到状态，超时信号防止失败测试无限等待。</summary>
    /// <param name="condition">目标状态。</param><param name="token">测试超时。</param><returns>达到目标时完成。</returns>
    private static async Task WaitForBashConditionAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }
}
