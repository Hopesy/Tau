// 作者：xxx
using System.Threading.Channels;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentCompositionTurnInputTests
{
    /// <summary>【CodingAgent】【Unicode 流式提交】单字节终端分包仍完整传递表情 steering 与中文 follow-up。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task UnicodeRawStream_PreservesSteeringAndFollowUpText()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("👩🏽‍💻\r中文😀\u0011"));
        using var reader = new TuiDecodedKeyReader(new TuiStreamRawInputReader(stream, bufferSize: 1));
        var source = new CompositionCodingAgentTurnInputSource(reader, new(new Surface(), reader), Native());
        await using var inputs = source.ReadInputsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.Steering, "👩🏽‍💻"), inputs.Current);
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.FollowUp, "中文😀"), inputs.Current);
    }

    /// <summary>【CodingAgent】【流式发送】原生 Shift+Enter 保留换行，Windows Ctrl+Q 发送 follow-up，普通提交恢复 steering。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task NativeBindings_PreserveMultilineAndFollowUpClassification()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = new Events(); reader.Text("first"); reader.Key(ConsoleKey.Enter, ConsoleModifiers.Shift); reader.Text("second");
        reader.Key(ConsoleKey.Q, ConsoleModifiers.Control); reader.Text("third"); reader.Key(ConsoleKey.Enter);
        var source = Create(reader, Native());
        await using var inputs = source.ReadInputsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.FollowUp, "first\nsecond"), inputs.Current);
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.Steering, "third"), inputs.Current);
    }

    /// <summary>【CodingAgent】【自定义发送】覆盖后的发送键决定消息类型，原 Enter 不再强制提交。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task CustomBindings_UseConfiguredSubmissionAndIgnoreOldEnter()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = new Events(); reader.Text("a"); reader.Key(ConsoleKey.Enter); reader.Text("b"); reader.Key(ConsoleKey.F3);
        reader.Text("c"); reader.Key(ConsoleKey.F2);
        var source = Create(reader, Native("""{"tui.input.submit":"f2","app.message.followUp":"f3"}"""));
        await using var inputs = source.ReadInputsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.FollowUp, "ab"), inputs.Current);
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.Steering, "c"), inputs.Current);
    }

    /// <summary>【CodingAgent】【粘贴透传】多行 bracketed-paste 原样交给输入编辑器，提交时展开折叠标记。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task PasteEvents_PreserveCompleteTextThroughFollowUp()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = string.Join('\n', Enumerable.Range(1, 15).Select(index => "line " + index));
        var reader = new Events(); reader.Paste(text); reader.Key(ConsoleKey.Q, ConsoleModifiers.Control);
        await using var inputs = Create(reader, Native()).ReadInputsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.FollowUp, text), inputs.Current);
        Assert.Equal(0, reader.LegacyKeyReads);
    }

    /// <summary>【CodingAgent】【实时配置】替换主编辑器的绑定对象后，流式输入区使用新对象，无需重建。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ConfigurationProvider_TracksReplacedBindings()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = new Events(); reader.Text("one"); reader.Key(ConsoleKey.F2);
        IKeyBindingMap current = Native("""{"tui.input.submit":"f2"}""");
        var session = new TuiCompositionSession(new Surface(), reader);
        var source = new CompositionCodingAgentTurnInputSource(reader, session, keyBindingsProvider: () => current);
        await using var inputs = source.ReadInputsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal("one", inputs.Current.Text);
        current = Native("""{"tui.input.submit":"f4","app.message.followUp":"f5"}""");
        reader.Text("two"); reader.Key(ConsoleKey.F2); reader.Text("three"); reader.Key(ConsoleKey.F5);
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.FollowUp, "twothree"), inputs.Current);
    }

    /// <summary>【CodingAgent】【动作透传】流式应用快捷键作为动作交回宿主，不作为普通字符插入文本。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ApplicationActions_DoNotInsertTheirPrintableKeys()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = new Events(); reader.Text("qx"); reader.Key(ConsoleKey.Enter);
        await using var inputs = Create(reader, Native("""{"app.model.select":"q"}""")).ReadInputsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(EditorAction.SelectModel, inputs.Current.Action);
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal("x", inputs.Current.Text);
    }

    /// <summary>【CodingAgent】【清空键覆盖】流式草稿遵守当前清空键配置，取消空输入等待不会产生消息。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ClearAndCancellation_PreserveConfiguredInputLifecycle()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = new Events(); reader.Text("discard"); reader.Key(ConsoleKey.F4); reader.Text("keep"); reader.Key(ConsoleKey.Enter);
        var source = Create(reader, Native("""{"app.clear":"f4"}"""));
        await using var inputs = source.ReadInputsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(EditorAction.ClearEditor, inputs.Current.Action);
        source.SetDraft(string.Empty);
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal("keep", inputs.Current.Text);
        var waiting = inputs.MoveNextAsync().AsTask(); await deadline.CancelAsync(); Assert.False(await waiting);
    }

    /// <summary>【CodingAgent】【旧格式兼容】旧绑定继续保留 Alt+Enter 发送 follow-up 的行为。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task LegacyBindings_RetainAltEnterFollowUp()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = new Events(); reader.Text("legacy"); reader.Key(ConsoleKey.Enter, ConsoleModifiers.Alt);
        await using var inputs = Create(reader, KeyBindingMap.Default).ReadInputsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.FollowUp, "legacy"), inputs.Current);
    }

    /// <summary>【CodingAgent】【协议发送分类】流式输入保留 Super 和 Shift 的原始身份，不把高级协议退化为普通提交。</summary>
    /// <param name="followUpKey">follow-up 的原始终端协议。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("\u001b[113;9u")]
    [InlineData("\u001b[27;9;113~")]
    public async Task RawBindings_PreserveFollowUpSubmitAndInterruptIdentity(string followUpKey)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = new Events(); reader.Text("follow"); reader.Raw(followUpKey);
        reader.Text("steer"); reader.Raw("S"); reader.Text("draft"); reader.Raw("\u001b[120;9u");
        var bindings = Native("""{"app.message.followUp":"super+q","tui.input.submit":"shift+s","app.interrupt":"super+x"}""");
        await using var inputs = Create(reader, bindings).ReadInputsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.FollowUp, "follow"), inputs.Current);
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.Steering, "steer"), inputs.Current);
        Assert.True(await inputs.MoveNextAsync()); Assert.Equal(new(CodingAgentTurnInputKind.Interrupt, "draft"), inputs.Current);
    }

    /// <summary>【CodingAgent】【流式夹具】创建带指定配置的组合输入源。</summary>
    /// <param name="reader">事件来源。</param><param name="bindings">输入键位。</param><returns>输入源。</returns>
    private static CompositionCodingAgentTurnInputSource Create(Events reader, IKeyBindingMap bindings) =>
        new(reader, new TuiCompositionSession(new Surface(), reader), bindings);

    /// <summary>【CodingAgent】【原生键位】创建 Windows 应用的独立命名快捷键。</summary>
    /// <param name="json">覆盖 JSON。</param><returns>编辑器映射。</returns>
    private static IKeyBindingMap Native(string json = "{}") => CodingAgentKeybindings.CreateEditorBindings(
        new(CodingAgentKeybindings.ParseUserConfiguration(json), platform: "win32"));

    /// <summary>【CodingAgent】【事件队列】同时支持按键和原始粘贴，空队列等待取消。</summary>
    private sealed class Events : IConsoleInputEventReader
    {
        private readonly Channel<ConsoleInputEvent> _events = Channel.CreateUnbounded<ConsoleInputEvent>();
        public int LegacyKeyReads { get; private set; }
        public Task Gate { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>【CodingAgent】【字符输入】加入普通 ASCII 文本。</summary><param name="text">文本。</param>
        public void Text(string text)
        {
            foreach (var character in text)
            {
                TuiConsoleKeyInfoMapper.TryMapInput(character.ToString(), out var key);
                _events.Writer.TryWrite(ConsoleInputEvent.KeyPress(key));
            }
        }
        /// <summary>【CodingAgent】【按键输入】加入带修饰键的按键。</summary><param name="key">键名。</param><param name="modifiers">修饰键。</param>
        public void Key(ConsoleKey key, ConsoleModifiers modifiers = ConsoleModifiers.None) => _events.Writer.TryWrite(ConsoleInputEvent.KeyPress(
            new(key == ConsoleKey.Enter ? '\r' : '\0', key, modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt), modifiers.HasFlag(ConsoleModifiers.Control))));
        /// <summary>【CodingAgent】【粘贴输入】加入不可拆分的粘贴事件。</summary><param name="text">粘贴文本。</param>
        public void Paste(string text) => _events.Writer.TryWrite(ConsoleInputEvent.Paste(text));
        /// <summary>【CodingAgent】【原始事件】保留终端协议和兼容按键表示。</summary><param name="input">原始输入。</param>
        public void Raw(string input)
        {
            TuiConsoleKeyInfoMapper.TryMapInput(input, out var key);
            _events.Writer.TryWrite(ConsoleInputEvent.KeyPress(key, input));
        }
        /// <summary>【CodingAgent】【事件读取】等待下一事件。</summary><param name="cancellationToken">取消信号。</param><returns>完整事件。</returns>
        public async ValueTask<ConsoleInputEvent> ReadInputEventAsync(CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken);
            if (_events.Reader.TryRead(out var input)) return input;
            Waiting.TrySetResult();
            return await _events.Reader.ReadAsync(cancellationToken);
        }
        /// <summary>【CodingAgent】【旧读取探测】统计是否错误走单按键路径。</summary><param name="cancellationToken">取消信号。</param><returns>事件中的按键。</returns>
        public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        { LegacyKeyReads++; return (await ReadInputEventAsync(cancellationToken)).Key; }
    }

    /// <summary>【CodingAgent】【组合渲染夹具】提供固定视口。</summary>
    private sealed class Surface : ITuiRenderSurface
    {
        public int Width => 80;
        public int Height => 24;
        /// <summary>【CodingAgent】【测试渲染】消费差异帧。</summary><param name="diff">帧差异。</param>
        public void Apply(TuiRenderDiff diff) { }
    }
}
