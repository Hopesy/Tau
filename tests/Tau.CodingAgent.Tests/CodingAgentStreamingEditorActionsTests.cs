// 作者：xxx
using System.Runtime.CompilerServices;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentCompositionTurnInputTests
{
    /// <summary>【CodingAgent】【生成中粘贴】剪贴板操作当前流式光标，自然结束后光标随草稿回到主编辑器。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task StreamingPaste_UsesActiveDraftAndTransfersCursorToMainEditor()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task }; stream.Text("ab"); stream.Key(ConsoleKey.LeftArrow); stream.Key(ConsoleKey.V, ConsoleModifiers.Alt);
        var runner = new FakeCodingAgentRunner((input, token) => input == "start" ? WaitForDraftAsync(started, stream.Waiting.Task, false, token) : AsyncEnumerable.Empty<AgentEvent>());
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Text("!"); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main))); editor.SetExpandedDraft("start");
        var clipboard = new FakeCodingAgentClipboard { Text = "中" }; var ui = new InteractiveConsoleSession(new FakeTerminal(), editor);
        var source = Create(stream, Native());
        var host = new CodingAgentHost(ui, runner, clipboard: clipboard, turnInputSource: source);
        await host.RunAsync(deadline.Token);
        Assert.Equal(["start", "a中!b"], runner.Inputs); Assert.Equal(["files", "image", "text"], clipboard.Reads);
        Assert.Equal(string.Empty, source.InputEditor.GetExpandedDraft()); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【粘贴期间回合结束】模型完成时取消尚在等待的剪贴板读取，并把未修改草稿交还主编辑器。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task TurnEndingDuringClipboardRead_RestoresDraftOwnership()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var clipboard = new WaitingClipboard();
        var stream = new Events { Gate = started.Task }; stream.Text("draft"); stream.Key(ConsoleKey.V, ConsoleModifiers.Alt);
        var runner = new FakeCodingAgentRunner((_, token) => WaitForDraftAsync(started, clipboard.Started.Task, false, token));
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main))); editor.SetExpandedDraft("start");
        var terminal = new FakeTerminal(); var ui = new InteractiveConsoleSession(terminal, editor); var source = Create(stream, Native());
        await new CodingAgentHost(ui, runner, clipboard: clipboard, turnInputSource: source).RunAsync(deadline.Token);
        Assert.Equal("draft", ui.GetDraft()); Assert.Equal(string.Empty, source.InputEditor.GetExpandedDraft());
        Assert.DoesNotContain("turn input listener failed", terminal.FlattenedText()); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【生成中显示操作】工具、思考、复制和思考等级动作不清空流式草稿，也不发送额外消息。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task StreamingDisplayAndCopyActions_PreserveDraftAndCurrentTurn()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task }; stream.Text("draft");
        stream.Key(ConsoleKey.F2); stream.Key(ConsoleKey.F3); stream.Key(ConsoleKey.F4); stream.Key(ConsoleKey.F5);
        var runner = new FakeCodingAgentRunner((_, token) => WaitWithToolAsync(started, stream.Waiting.Task, token));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("copy this")]));
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main))); editor.SetExpandedDraft("start");
        var terminal = new FakeTerminal(); var clipboard = new FakeCodingAgentClipboard(); var ui = new InteractiveConsoleSession(terminal, editor);
        var source = Create(stream, Native("""{"app.tools.expand":"f2","app.thinking.toggle":"f3","app.message.copy":"f4","app.thinking.cycle":"f5"}"""));
        await new CodingAgentHost(ui, runner, clipboard: clipboard, turnInputSource: source).RunAsync(deadline.Token);
        Assert.Equal("draft", ui.GetDraft()); Assert.Equal(["copy this"], clipboard.CopiedTexts); Assert.NotNull(runner.ThinkingLevel);
        Assert.Equal(["start"], runner.Inputs); Assert.Contains("tool output: expanded", terminal.FlattenedText());
        Assert.Contains("thinking blocks: hidden", terminal.FlattenedText()); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【生成中清空】清空只修改当前流式草稿，随后输入仍可发送到同一回合。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task StreamingClear_RemovesOnlyDraftAndAllowsNextSteering()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task }; stream.Text("discard"); stream.Key(ConsoleKey.C, ConsoleModifiers.Control); stream.Text("keep"); stream.Key(ConsoleKey.Enter);
        var runner = new FakeCodingAgentRunner((_, token) => WaitForRestoredSubmissionAsync(started, submitted.Task, token));
        runner.SteeringObserver = text => submitted.TrySetResult(text);
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main))); editor.SetExpandedDraft("start");
        await new CodingAgentHost(new(new FakeTerminal(), editor), runner, turnInputSource: Create(stream, Native())).RunAsync(deadline.Token);
        Assert.Equal("keep", await submitted.Task); Assert.Equal(["start"], runner.Inputs); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【生成中退出】双清空或空输入退出键取消当前回合，停止剩余启动消息并结束宿主。</summary>
    /// <param name="doubleClear">是否使用双清空。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingExit_StopsCurrentTurnAndRemainingStartupMessages(bool doubleClear)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task };
        if (doubleClear) { stream.Text("draft"); stream.Key(ConsoleKey.C, ConsoleModifiers.Control); stream.Key(ConsoleKey.C, ConsoleModifiers.Control); }
        else stream.Key(ConsoleKey.D, ConsoleModifiers.Control);
        var runner = new FakeCodingAgentRunner((_, token) => WaitForDraftAsync(started, Task.CompletedTask, true, token));
        var main = new Events(); var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)));
        var terminal = new FakeTerminal();
        var host = new CodingAgentHost(new(terminal, editor), runner, turnInputSource: Create(stream, Native()), initialMessages: ["start", "must not run"])
        { EditorKeyTimeProvider = new StreamingFixedClock() };
        Assert.Equal(0, await host.RunAsync(deadline.Token)); Assert.Equal(["start"], runner.Inputs);
        Assert.Contains("Goodbye!", terminal.FlattenedText()); Assert.False(main.Waiting.Task.IsCompleted); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【非空删除】流式非空草稿的 Ctrl+D 继续执行删除，而非退出动作。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task StreamingExitKey_DeletesCharacterWhenDraftIsNotEmpty()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task }; stream.Text("ab"); stream.Key(ConsoleKey.Home); stream.Key(ConsoleKey.D, ConsoleModifiers.Control); stream.Key(ConsoleKey.Enter);
        var runner = new FakeCodingAgentRunner((_, token) => WaitForRestoredSubmissionAsync(started, submitted.Task, token)); runner.SteeringObserver = text => submitted.TrySetResult(text);
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main))); editor.SetExpandedDraft("start");
        await new CodingAgentHost(new(new FakeTerminal(), editor), runner, turnInputSource: Create(stream, Native())).RunAsync(deadline.Token);
        Assert.Equal("b", await submitted.Task); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【工具显示夹具】工具处于运行状态时处理显示动作，全部按键完成后结束工具。</summary>
    /// <param name="started">按键启动屏障。</param><param name="actionsRead">按键完成屏障。</param><param name="token">回合信号。</param><returns>运行事件。</returns>
    private static async IAsyncEnumerable<AgentEvent> WaitWithToolAsync(TaskCompletionSource started, Task actionsRead, [EnumeratorCancellation] CancellationToken token)
    {
        yield return new ToolExecutionStartEvent("clipboard-tool", "read"); started.TrySetResult();
        await actionsRead.WaitAsync(token);
        yield return new ToolExecutionEndEvent("clipboard-tool", new ToolResult([new TextContent("done")]), "read");
        yield return new AgentEndEvent();
    }

    /// <summary>【CodingAgent】【退出计时夹具】固定时间用于验证双清空，不依赖线程调度速度。</summary>
    private sealed class StreamingFixedClock : TimeProvider
    {
        /// <summary>【CodingAgent】【固定时间】返回固定 UTC 时间。</summary><returns>固定时间。</returns>
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(1);
    }

    /// <summary>【CodingAgent】【阻塞剪贴板夹具】在文件读取入口等待回合取消，验证草稿路由释放。</summary>
    private sealed class WaitingClipboard : ICodingAgentClipboard
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>【CodingAgent】【写入夹具】忽略写入。</summary><param name="text">文本。</param><param name="cancellationToken">取消信号。</param><returns>完成任务。</returns>
        public Task SetTextAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        /// <summary>【CodingAgent】【图片夹具】没有图片。</summary><param name="cancellationToken">取消信号。</param><returns>空图片。</returns>
        public Task<CodingAgentClipboardImage?> ReadImageAsync(CancellationToken cancellationToken = default) => Task.FromResult<CodingAgentClipboardImage?>(null);
        /// <summary>【CodingAgent】【文件等待】通知读取已开始并等待取消。</summary><param name="cancellationToken">回合信号。</param><returns>取消的读取任务。</returns>
        public async Task<IReadOnlyList<string>?> ReadFilePathsAsync(CancellationToken cancellationToken = default)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return null; }
    }
}
