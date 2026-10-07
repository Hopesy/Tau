// 作者：xxx
using System.Runtime.CompilerServices;
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentCompositionTurnInputTests
{
    /// <summary>【CodingAgent】【生成中断】原生与旧版 Escape 均发布中断动作，并保留完整多行粘贴草稿。</summary>
    /// <param name="legacy">是否使用旧格式。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Escape_PublishesInterruptWithoutClearingDraft(bool legacy)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = string.Join('\n', Enumerable.Repeat("  pasted text  ", 15));
        var reader = new Events(); reader.Paste(text); reader.Key(ConsoleKey.Escape);
        var source = Create(reader, legacy ? KeyBindingMap.Default : Native());
        await using var inputs = source.ReadInputsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await inputs.MoveNextAsync());
        Assert.Equal(new(CodingAgentTurnInputKind.Interrupt, text), inputs.Current);
        Assert.Equal(text, source.TakeDraft()); Assert.Equal(string.Empty, source.TakeDraft());
    }

    /// <summary>【CodingAgent】【中断键覆盖】覆盖 Escape 后只有新绑定中断，原按键不会丢弃输入。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Interrupt_UsesCustomBinding()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = new Events(); reader.Text("a"); reader.Key(ConsoleKey.Escape); reader.Text("b"); reader.Key(ConsoleKey.F4);
        await using var inputs = Create(reader, Native("""{"app.interrupt":"f4"}""")).ReadInputsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.Interrupt, "ab"), inputs.Current);
    }

    /// <summary>【CodingAgent】【队列取回】动作事件允许宿主替换草稿，下一次提交仅发送完整恢复结果。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Dequeue_AllowsEditingRestoredMessagesBeforeSubmit()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = new Events(); reader.Text("draft"); reader.Key(ConsoleKey.Q, ConsoleModifiers.Alt); reader.Key(ConsoleKey.Enter);
        var source = Create(reader, Native());
        await using var inputs = source.ReadInputsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.RestoreQueuedMessages, "draft"), inputs.Current);
        source.SetDraft("queued\n\ndraft");
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.Steering, "queued\n\ndraft"), inputs.Current);
        Assert.Equal(string.Empty, source.TakeDraft());
    }

    /// <summary>【CodingAgent】【自然结束草稿】取消输入等待后完整展开草稿，清除旧粘贴表后恢复的文本不会再次展开。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Cancellation_TransfersPasteDraftOnceWithoutReexpansion()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var text = string.Join('\n', Enumerable.Repeat("[paste #1 +15 lines]", 15));
        var reader = new Events(); reader.Paste(text);
        var source = Create(reader, Native());
        await using var inputs = source.ReadInputsAsync(inputLifetime.Token).GetAsyncEnumerator();
        var waiting = inputs.MoveNextAsync().AsTask();
        await reader.Waiting.Task.WaitAsync(deadline.Token); await inputLifetime.CancelAsync();
        Assert.False(await waiting); Assert.Equal(text, source.TakeDraft());
        source.SetDraft(text); Assert.Equal(text, source.TakeDraft()); Assert.Equal(string.Empty, source.TakeDraft());
    }

    /// <summary>【CodingAgent】【回合交接】中断取回未消费队列，自然结束保留草稿；宿主随后仍能提交新回合。</summary>
    /// <param name="interrupt">是否显式中断。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_TransfersDraftAndContinuesAfterTurn(bool interrupt)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Events { Gate = started.Task }; reader.Text("unsent");
        if (interrupt) reader.Key(ConsoleKey.Escape);
        var runner = new FakeCodingAgentRunner((input, token) => input == "start"
            ? WaitForDraftAsync(started, reader.Waiting.Task, interrupt, token) : AsyncEnumerable.Empty<AgentEvent>());
        if (interrupt) { runner.Steer("consumed"); runner.Steer("pending"); runner.FollowUp("later"); }
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)));
        editor.Buffer.SetDraft("start");
        var host = new CodingAgentHost(new(new FakeTerminal(), editor), runner, turnInputSource: Create(reader, Native()));
        await host.RunAsync(deadline.Token);
        Assert.Equal(["start", interrupt ? "pending\n\nlater\n\nunsent" : "unsent"], runner.Inputs);
        Assert.Equal(0, runner.PendingMessageCount); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【在线取回】取回队列后可在同一回合继续编辑并重新发送，不会取消正在生成的回答。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Host_DequeueRestoresOnlyPendingMessagesWithoutInterrupting()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeCodingAgentRunner((_, token) => WaitForRestoredSubmissionAsync(started, submitted.Task, token));
        runner.Steer("consumed"); runner.Steer("pending"); runner.FollowUp("later");
        runner.SteeringObserver = text => submitted.TrySetResult(text);
        var reader = new Events { Gate = started.Task }; reader.Text("draft"); reader.Key(ConsoleKey.Q, ConsoleModifiers.Alt); reader.Key(ConsoleKey.Enter);
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)));
        editor.Buffer.SetDraft("start");
        var host = new CodingAgentHost(new(new FakeTerminal(), editor), runner, turnInputSource: Create(reader, Native()));
        await host.RunAsync(deadline.Token);
        Assert.Equal("pending\n\nlater\n\ndraft", await submitted.Task); Assert.Equal(0, runner.PendingMessageCount);
        Assert.Equal(["start"], runner.Inputs); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【空草稿中断】中断动作没有文本也必须取消当前回合，不能被空消息过滤吞掉。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Host_EmptyInterruptStillStopsTheTurn()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Events { Gate = started.Task }; reader.Key(ConsoleKey.Escape);
        var runner = new FakeCodingAgentRunner((_, token) => WaitForDraftAsync(started, Task.CompletedTask, true, token));
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)));
        editor.Buffer.SetDraft("start");
        var terminal = new FakeTerminal();
        var host = new CodingAgentHost(new(terminal, editor), runner, turnInputSource: Create(reader, Native()));
        await host.RunAsync(deadline.Token);
        Assert.Contains("[Cancelled]", terminal.FlattenedText()); Assert.Equal(["start"], runner.Inputs);
        Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【取回屏障】等待重新提交恢复消息，确认等待期间没有取消并发布消费事件。</summary>
    /// <param name="started">流式输入启动屏障。</param><param name="submitted">重新提交的文本。</param>
    /// <param name="token">回合取消信号。</param><returns>受控运行事件。</returns>
    private static async IAsyncEnumerable<AgentEvent> WaitForRestoredSubmissionAsync(TaskCompletionSource started,
        Task<string> submitted, [EnumeratorCancellation] CancellationToken token)
    {
        yield return new MessageStartEvent(new UserMessage("consumed"));
        started.TrySetResult();
        var text = await submitted.WaitAsync(token); Assert.False(token.IsCancellationRequested);
        yield return new MessageStartEvent(new UserMessage(text));
        yield return new AgentEndEvent();
    }

    /// <summary>【CodingAgent】【真实取消链】中断传到模型信号并保留中止回答，恢复的草稿可开启正常新回合。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Host_InterruptReachesProviderAndPreservesAbortedHistory()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var provider = new InterruptProvider();
        var registry = new ProviderRegistry(); registry.Register(provider.Api, () => provider);
        var catalog = new ModelCatalog(); catalog.RegisterModel(new Model { Provider = "test", Id = "model", Name = "Test", Api = provider.Api });
        var runner = RuntimeCodingAgentRunner.Create("test", "model", toolsOverride: [], providerRegistryOverride: registry,
            modelCatalogOverride: catalog, apiKey: "synthetic");
        var reader = new Events { Gate = provider.Started.Task }; reader.Text("next"); reader.Key(ConsoleKey.Escape);
        var main = new Events(); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.Enter); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)));
        editor.Buffer.SetDraft("start");
        var host = new CodingAgentHost(new(new FakeTerminal(), editor), runner, turnInputSource: Create(reader, Native()));
        await host.RunAsync(deadline.Token);
        Assert.True(provider.Cancelled); Assert.Equal(2, provider.Calls); Assert.False(runner.IsStreaming);
        Assert.Equal(["start", "next"], runner.Messages.OfType<UserMessage>().Select(message => Assert.Single(message.Content.OfType<TextContent>()).Text));
        Assert.Contains(runner.Messages.OfType<AssistantMessage>(), message => message.StopReason == StopReason.Aborted);
        Assert.Contains(runner.Messages.OfType<AssistantMessage>(), message => message.Content.OfType<TextContent>().Any(text => text.Text == "completed"));
        Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【受控回合】先发布已消费输入，再等待输入完成或主动取消。</summary>
    /// <param name="started">输入开始屏障。</param><param name="draftRead">草稿读取完毕信号。</param>
    /// <param name="interrupt">是否等待取消。</param><param name="token">回合信号。</param><returns>运行事件。</returns>
    private static async IAsyncEnumerable<AgentEvent> WaitForDraftAsync(TaskCompletionSource started, Task draftRead,
        bool interrupt, [EnumeratorCancellation] CancellationToken token)
    {
        yield return new MessageStartEvent(new UserMessage("consumed"));
        started.TrySetResult();
        if (interrupt) await Task.Delay(Timeout.Infinite, token);
        else await draftRead.WaitAsync(token);
        yield return new AgentEndEvent();
    }

    /// <summary>【CodingAgent】【模型中断夹具】首轮等待真实取消信号，第二轮立即完成。</summary>
    private sealed class InterruptProvider : IStreamProvider
    {
        public string Api => "turn-interrupt-test";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public int Calls { get; private set; }
        /// <summary>【CodingAgent】【模型流】创建受控标准请求。</summary>
        /// <param name="model">模型。</param><param name="context">上下文。</param><param name="options">请求选项。</param><returns>事件流。</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) => Create(options.Signal);
        /// <summary>【CodingAgent】【简化模型流】创建受控简化请求。</summary>
        /// <param name="model">模型。</param><param name="context">上下文。</param><param name="options">请求选项。</param><returns>事件流。</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Create(options.Signal);
        /// <summary>【CodingAgent】【请求取消】捕获回合信号，并在取消时发布中止结果。</summary>
        /// <param name="token">模型接收的取消信号。</param><returns>模型事件流。</returns>
        private AssistantMessageStream Create(CancellationToken token)
        {
            var stream = new AssistantMessageStream();
            if (++Calls == 1)
            {
                token.Register(() => { Cancelled = true; stream.Push(new DoneEvent(new AssistantMessage([new TextContent("partial")]) { StopReason = StopReason.Aborted })); });
                Started.TrySetResult();
            }
            else stream.Push(new DoneEvent(new AssistantMessage([new TextContent("completed")])));
            return stream;
        }
    }
}
