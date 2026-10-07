// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public partial class CodingAgentHostTests
{
    /// <summary>【CodingAgent】【清空输入】第一次 Ctrl+C 清空草稿并继续接收输入，Ctrl+D 在空输入时退出。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task NamedHostKeys_ClearContinuesAndEmptyExitStops()
    {
        var keys = NativeHostReader(NativeControl(ConsoleKey.C), new('o', ConsoleKey.O, false, false, false),
            new('k', ConsoleKey.K, false, false, false), new('\r', ConsoleKey.Enter, false, false, false), NativeControl(ConsoleKey.D));
        var editor = NativeHostEditor(keys); editor.Buffer.SetDraft("discard");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var terminal = new FakeTerminal();
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal, editor), runner);
        Assert.Equal(0, await host.RunAsync());
        Assert.Equal(["ok"], runner.Inputs);
        Assert.Contains("Goodbye!", terminal.FlattenedText());
    }

    /// <summary>【CodingAgent】【快速退出】两次清空间隔严格小于 500 毫秒才退出，边界值继续编辑。</summary>
    /// <param name="delay">两次动作间隔。</param><param name="exits">是否立即退出。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(499, true)]
    [InlineData(500, false)]
    public async Task NamedHostKeys_DoubleClearUsesExactWindow(int delay, bool exits)
    {
        var clock = new NativeEditorClock();
        var keys = new NativeTimedReader(clock, [
            (NativeControl(ConsoleKey.C), 0), (NativeControl(ConsoleKey.C), delay),
            (new('a', ConsoleKey.A, false, false, false), 0), (new('\r', ConsoleKey.Enter, false, false, false), 0), (NativeControl(ConsoleKey.D), 0)]);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(new InteractiveConsoleSession(new FakeTerminal(), NativeHostEditor(keys)), runner) { EditorKeyTimeProvider = clock };
        await host.RunAsync();
        Assert.Equal(exits ? [] : new[] { "a" }, runner.Inputs);
    }

    /// <summary>【CodingAgent】【普通中断】Escape 保留普通草稿，bash 草稿则被清空，均不会直接退出会话。</summary>
    /// <param name="bash">是否 bash 草稿。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedHostKeys_EscapePreservesNormalDraft(bool bash)
    {
        var keys = NativeHostReader(new('\u001b', ConsoleKey.Escape, false, false, false),
            new('\r', ConsoleKey.Enter, false, false, false), NativeControl(ConsoleKey.D));
        var editor = NativeHostEditor(keys); editor.Buffer.SetDraft(bash ? "!pwd" : "keep draft");
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        await new CodingAgentHost(new InteractiveConsoleSession(new FakeTerminal(), editor), runner).RunAsync();
        Assert.Equal(bash ? [] : new[] { "keep draft" }, runner.Inputs);
    }

    /// <summary>【CodingAgent】【应用动作】思考切换、复制末条回答和新建会话通过原有业务操作执行。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task NamedHostKeys_ThinkingCopyAndNewSessionExecute()
    {
        var manager = new CodingAgentKeybindings(CodingAgentKeybindings.ParseUserConfiguration("""{"app.session.new":"f2"}"""), platform: "win32");
        var keys = NativeHostReader(new('\t', ConsoleKey.Tab, true, false, false), NativeControl(ConsoleKey.X),
            new('\0', ConsoleKey.F2, false, false, false), NativeControl(ConsoleKey.D));
        var editor = NativeHostEditor(keys, manager);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("answer to copy")]));
        var clipboard = new FakeCodingAgentClipboard(); var terminal = new FakeTerminal();
        await new CodingAgentHost(new InteractiveConsoleSession(terminal, editor), runner, clipboard: clipboard).RunAsync();
        Assert.Equal(["answer to copy"], clipboard.CopiedTexts);
        Assert.Equal(1, runner.ResetSessionCalls);
        Assert.Contains("thinking:", terminal.FlattenedText());
        Assert.Empty(runner.Inputs);
    }

    /// <summary>【CodingAgent】【有文本退出键】默认 Ctrl+D 删除前方字符，自定义退出键在有文本时回退普通输入。</summary>
    /// <param name="custom">是否使用可打印退出键。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedHostKeys_ExitFallsThroughWhenTextExists(bool custom)
    {
        var manager = new CodingAgentKeybindings(custom ? CodingAgentKeybindings.ParseUserConfiguration("""{"app.exit":"q"}""") : null, platform: "win32");
        var keys = NativeHostReader(new('\0', ConsoleKey.Home, false, false, false),
            custom ? new('q', ConsoleKey.Q, false, false, false) : NativeControl(ConsoleKey.D), new('\r', ConsoleKey.Enter, false, false, false));
        var editor = NativeHostEditor(keys, manager); editor.Buffer.SetDraft("ab");
        var result = await editor.ReadLineAsync("> ");
        Assert.Equal(InputResultKind.Submitted, result.Kind);
        Assert.Equal(custom ? "qab" : "b", result.Text);
    }

    /// <summary>【CodingAgent】【历史优先级】显式 Ctrl+P 历史绑定优先于同键模型切换。</summary>
    [Fact]
    public void NamedHostKeys_ExplicitHistoryPrecedesModelCycle()
    {
        var manager = new CodingAgentKeybindings(CodingAgentKeybindings.ParseUserConfiguration("""{"tui.editor.historyPrevious":"ctrl+p"}"""));
        Assert.Equal(EditorAction.PromptHistoryPrevious, CodingAgentKeybindings.CreateEditorBindings(manager).Resolve(NativeControl(ConsoleKey.P)));
    }

    /// <summary>【CodingAgent】【补全取消】Escape 优先关闭当前补全，不向宿主发送中断动作，也不丢弃输入。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task NamedHostKeys_EscapeDismissesAutocompleteBeforeHostAction()
    {
        var keys = NativeHostReader(new('\t', ConsoleKey.Tab, false, false, false), new('\u001b', ConsoleKey.Escape, false, false, false),
            new('\r', ConsoleKey.Enter, false, false, false));
        var editor = NativeHostEditor(keys); editor.Buffer.SetDraft("/mo");
        editor.SetAutocompleteProvider(new TuiCombinedAutocompleteProvider([new TuiSlashCommand("model", "Model"), new TuiSlashCommand("models", "Models")], basePath: Environment.CurrentDirectory));
        var result = await editor.ReadLineAsync("> ");
        Assert.Equal(InputResultKind.Submitted, result.Kind); Assert.Equal("/model ", result.Text);
    }

    /// <summary>【CodingAgent】【宿主按键夹具】生成原生应用默认绑定的输入编辑器。</summary>
    /// <param name="reader">按键源。</param><param name="bindings">可选配置。</param><returns>输入编辑器。</returns>
    private static InteractiveInputEditor NativeHostEditor(IConsoleKeyReader reader, CodingAgentKeybindings? bindings = null) =>
        new(reader, new CapturingRenderer(), bindings: CodingAgentKeybindings.CreateEditorBindings(bindings ?? new(platform: "win32")));

    /// <summary>【CodingAgent】【按键队列】把固定输入加入已有测试读取器。</summary>
    /// <param name="keys">固定输入。</param><returns>读取器。</returns>
    private static ScriptedKeyReader NativeHostReader(params ConsoleKeyInfo[] keys)
    {
        var reader = new ScriptedKeyReader(); foreach (var key in keys) reader.EnqueueRaw(key); return reader;
    }

    /// <summary>【CodingAgent】【控制字符】构造字母对应的 Ctrl 组合键。</summary>
    /// <param name="key">字母键。</param><returns>控制台按键。</returns>
    private static ConsoleKeyInfo NativeControl(ConsoleKey key) => new((char)((int)key - (int)ConsoleKey.A + 1), key, false, false, true);

    /// <summary>【CodingAgent】【按键时钟】在输入屏障处推进时间，不依赖测试机器调度延迟。</summary>
    private sealed class NativeEditorClock : TimeProvider
    {
        public int Milliseconds { get; set; } = 1000;
        /// <summary>【CodingAgent】【测试时间】读取当前受控 UTC 时间。</summary><returns>固定起点及已推进时间。</returns>
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Milliseconds);
    }

    /// <summary>【CodingAgent】【定时按键】读取每个按键前推进受控时钟。</summary>
    /// <param name="clock">测试时钟。</param><param name="events">按键与时间增量。</param>
    private sealed class NativeTimedReader(NativeEditorClock clock, IReadOnlyList<(ConsoleKeyInfo Key, int Delay)> events) : IConsoleKeyReader
    {
        private readonly Queue<(ConsoleKeyInfo Key, int Delay)> _events = new(events);
        /// <summary>【CodingAgent】【定时读取】消费下一按键并推进动作发生时间。</summary>
        /// <param name="cancellationToken">取消信号。</param><returns>下一按键。</returns>
        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); var next = _events.Dequeue(); clock.Milliseconds += next.Delay;
            return ValueTask.FromResult(next.Key);
        }
    }
}
