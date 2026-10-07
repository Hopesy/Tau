// 作者：xxx
using Tau.AgentCore;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentCompositionTurnInputTests
{
    /// <summary>【CodingAgent】【跨阶段编辑状态】生成期间删除的文本在结束后仍可撤销或从剪切环取回。</summary>
    /// <param name="undo">是否使用撤销而非粘回。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedEditor_PreservesUndoAndKillRingAfterTurn(bool undo)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task }; stream.Text("keep"); stream.Key(ConsoleKey.W, ConsoleModifiers.Control);
        var runner = new FakeCodingAgentRunner((input, token) => input == "start" ? WaitForDraftAsync(started, stream.Waiting.Task, false, token) : AsyncEnumerable.Empty<AgentEvent>());
        var main = new Events(); main.Key(ConsoleKey.Enter);
        if (undo) main.Raw("\u001f"); else main.Key(ConsoleKey.Y, ConsoleModifiers.Control);
        main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var session = new TuiCompositionSession(new Surface(), main);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(session)); editor.SetExpandedDraft("start");
        var source = new CompositionCodingAgentTurnInputSource(stream, session, Native(), sharedEditor: editor);
        await new CodingAgentHost(new(new FakeTerminal(), editor), runner, turnInputSource: source).RunAsync(deadline.Token);
        Assert.Same(editor, source.InputEditor); Assert.Equal(["start", "keep"], runner.Inputs); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【跨阶段历史】生成中的历史回看包含刚提交的主输入，steering 写入同一历史集合。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task SharedEditor_UsesMainHistoryAndRecordsSteering()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task }; stream.Key(ConsoleKey.UpArrow); stream.Text(" again"); stream.Key(ConsoleKey.Enter);
        var runner = new FakeCodingAgentRunner((_, token) => WaitForRestoredSubmissionAsync(started, submitted.Task, token));
        runner.SteeringObserver = text => submitted.TrySetResult(text);
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var session = new TuiCompositionSession(new Surface(), main);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(session)); editor.SetExpandedDraft("start");
        var source = new CompositionCodingAgentTurnInputSource(stream, session, Native(), sharedEditor: editor);
        await new CodingAgentHost(new(new FakeTerminal(), editor), runner, turnInputSource: source).RunAsync(deadline.Token);
        Assert.Equal("start again", await submitted.Task); Assert.Equal("start again", editor.History.Peek(0)); Assert.Equal("start", editor.History.Peek(1));
        Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【粘贴连续性】回合结束后保留折叠粘贴及其引用，主编辑器仍可展开原始文本。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task SharedEditor_KeepsCollapsedPasteAfterNaturalCompletion()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task }; var text = new string('x', 1400); stream.Paste(text);
        var runner = new FakeCodingAgentRunner((_, token) => WaitForDraftAsync(started, stream.Waiting.Task, false, token));
        var main = new Events();
        var session = new TuiCompositionSession(new Surface(), main);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(session));
        var source = new CompositionCodingAgentTurnInputSource(stream, session, sharedEditor: editor);
        // 1. 【CodingAgent】【读取暂停】主读取器阻塞时检查折叠引用，最后由宿主取消结束测试
        var host = new CodingAgentHost(new(new FakeTerminal(), editor), runner, initialMessages: ["start"], turnInputSource: source);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var running = host.RunAsync(lifetime.Token);
        await main.Waiting.Task.WaitAsync(deadline.Token);
        // 2. 【CodingAgent】【活动引用】主输入恢复读取后仍持有相同折叠引用，取消只结束宿主
        Assert.Equal(text, editor.GetExpandedDraft()); Assert.NotEqual(text, editor.GetCollapsedDraft());
        await lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(deadline.Token));
        Assert.False(deadline.IsCancellationRequested);
    }
}
