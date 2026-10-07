// 作者：xxx
using Tau.AgentCore;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentCompositionTurnInputTests
{
    /// <summary>【CodingAgent】【生成中模型循环】前后切换立即更新选择，保留生成及未提交草稿。</summary>
    /// <param name="backward">是否后退循环。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingModelCycle_PreservesDraftAndRunningTurn(bool backward)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task }; stream.Text("draft"); stream.Key(ConsoleKey.F2);
        var runner = new FakeCodingAgentRunner((_, token) => WaitForDraftAsync(started, stream.Waiting.Task, false, token));
        runner.ConfigureAuth("openai", "google");
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main))); editor.SetExpandedDraft("start");
        var ui = new InteractiveConsoleSession(new FakeTerminal(), editor);
        var bindings = Native(backward ? """{"app.model.cycleBackward":"f2"}""" : """{"app.model.cycleForward":"f2"}""");
        await new CodingAgentHost(ui, runner, turnInputSource: Create(stream, bindings)).RunAsync(deadline.Token);
        Assert.Equal("google", runner.Model.Provider); Assert.Equal("draft", ui.GetDraft());
        Assert.Equal(["start"], runner.Inputs); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【模型选择归属】生成自然结束时选择器继续持有输入权，选择或取消完成后恢复草稿。</summary>
    /// <param name="cancelSelection">是否取消模型选择。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelSelector_SurvivesNaturalTurnCompletion(bool cancelSelection)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task }; stream.Text("draft"); stream.Key(ConsoleKey.F2);
        CancellationToken turnToken = default;
        var runner = new FakeCodingAgentRunner((_, token) => { turnToken = token; return WaitForDraftAsync(started, entered.Task, false, token); });
        runner.ConfigureAuth("openai", "google");
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main))); editor.SetExpandedDraft("start");
        var ui = new InteractiveConsoleSession(new FakeTerminal(), editor);
        var host = new CodingAgentHost(ui, runner, turnInputSource: Create(stream, Native("""{"app.model.select":"f2"}""")),
            modelSelector: async (state, token) =>
            {
                Assert.Equal("openai", state.CurrentModel.Provider); Assert.Equal("draft", ui.GetDraft()); entered.TrySetResult();
                await WaitForCancellationAsync(turnToken); Assert.False(token.IsCancellationRequested);
                Assert.False(stream.Waiting.Task.IsCompleted); Assert.False(main.Waiting.Task.IsCompleted);
                return cancelSelection ? null : "google/gemini-2.5-pro";
            });
        await host.RunAsync(deadline.Token);
        Assert.Equal(cancelSelection ? "openai" : "google", runner.Model.Provider); Assert.Equal("draft", ui.GetDraft());
        Assert.Equal(["start"], runner.Inputs); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【外部编辑归属】生成结束不取消外部编辑，结果回填活动草稿且不自动发送。</summary>
    /// <param name="edited">是否保存修改。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalEditor_SurvivesNaturalTurnCompletion(bool edited)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task }; stream.Paste(new string('x', 1200)); stream.Key(ConsoleKey.F2);
        CancellationToken turnToken = default;
        var runner = new FakeCodingAgentRunner((_, token) => { turnToken = token; return WaitForDraftAsync(started, entered.Task, false, token); });
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main))); editor.SetExpandedDraft("start");
        var ui = new InteractiveConsoleSession(new FakeTerminal(), editor);
        var external = new StreamingExternalEditor(async (draft, token) =>
        {
            Assert.Equal(new string('x', 1200), draft); entered.TrySetResult();
            await WaitForCancellationAsync(turnToken); Assert.False(token.IsCancellationRequested);
            return new(true, edited, edited ? "edited\n草稿" : null);
        });
        await new CodingAgentHost(ui, runner, externalEditor: external,
            turnInputSource: Create(stream, Native("""{"app.editor.external":"f2"}"""))).RunAsync(deadline.Token);
        Assert.Equal(edited ? "edited\n草稿" : new string('x', 1200), ui.GetDraft());
        Assert.Equal(["start"], runner.Inputs); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【动作异常隔离】模型选择或外部编辑失败后仍能提交 steering，不终止输入监听。</summary>
    /// <param name="external">是否测试外部编辑器。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingActionFailure_AllowsFurtherSteering(bool external)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task }; stream.Text("draft"); stream.Key(ConsoleKey.F2); stream.Key(ConsoleKey.Enter);
        var runner = new FakeCodingAgentRunner((_, token) => WaitForRestoredSubmissionAsync(started, submitted.Task, token));
        runner.ConfigureAuth("openai", "google"); runner.SteeringObserver = text => submitted.TrySetResult(text);
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main))); editor.SetExpandedDraft("start");
        var terminal = new FakeTerminal();
        var bindings = Native(external ? """{"app.editor.external":"f2"}""" : """{"app.model.select":"f2"}""");
        await new CodingAgentHost(new(terminal, editor), runner, turnInputSource: Create(stream, bindings),
            externalEditor: new StreamingExternalEditor((_, _) => throw new IOException("external failed")),
            modelSelector: (_, _) => throw new IOException("selector failed")).RunAsync(deadline.Token);
        Assert.Equal("draft", await submitted.Task); Assert.Contains(external ? "external failed" : "selector failed", terminal.FlattenedText());
        Assert.DoesNotContain("turn input listener failed", terminal.FlattenedText()); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【宿主取消】长时间模型选择或外部编辑遵守宿主取消并释放草稿归属。</summary>
    /// <param name="external">是否测试外部编辑器。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostCancellation_EndsModalActionAndRestoresDraft(bool external)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Events { Gate = started.Task }; stream.Text("draft"); stream.Key(ConsoleKey.F2);
        var runner = new FakeCodingAgentRunner((_, token) => WaitForDraftAsync(started, Task.CompletedTask, true, token));
        runner.ConfigureAuth("openai", "google");
        var main = new Events(); var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)));
        var ui = new InteractiveConsoleSession(new FakeTerminal(), editor);
        var bindings = Native(external ? """{"app.editor.external":"f2"}""" : """{"app.model.select":"f2"}""");
        var host = new CodingAgentHost(ui, runner, initialMessages: ["start"], turnInputSource: Create(stream, bindings),
            externalEditor: new StreamingExternalEditor(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(true, false, null); }),
            modelSelector: async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return null; });
        var running = host.RunAsync(lifetime.Token); await entered.Task.WaitAsync(deadline.Token); await lifetime.CancelAsync();
        await running.WaitAsync(deadline.Token);
        Assert.Equal("draft", ui.GetDraft()); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【取消屏障】等待回合自然完成发出的监听取消信号。</summary>
    /// <param name="token">回合监听信号。</param><returns>取消发生后完成的任务。</returns>
    private static async Task WaitForCancellationAsync(CancellationToken token)
    {
        try { await Task.Delay(Timeout.Infinite, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>【CodingAgent】【外部编辑夹具】用可控异步处理器模拟外部编辑器。</summary>
    /// <param name="handler">接收完整草稿及生命周期信号的处理器。</param>
    private sealed class StreamingExternalEditor(Func<string, CancellationToken, Task<CodingAgentExternalEditorResult>> handler) : ICodingAgentExternalEditor
    {
        /// <summary>【CodingAgent】【编辑调用】转交草稿和取消信号。</summary>
        /// <param name="currentText">展开草稿。</param><param name="cancellationToken">宿主信号。</param><returns>编辑结果。</returns>
        public Task<CodingAgentExternalEditorResult> EditAsync(string currentText, CancellationToken cancellationToken = default) => handler(currentText, cancellationToken);
    }
}
