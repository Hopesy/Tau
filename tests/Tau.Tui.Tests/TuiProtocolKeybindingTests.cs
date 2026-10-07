// 作者：xxx
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

[Collection(TuiTestCollections.TuiKeyDecoderState)]
public sealed class TuiProtocolKeybindingTests
{
    /// <summary>【Tui】【完整协议键位】主编辑器通过原始协议匹配 Shift、Super、布局回退与锁定状态。</summary>
    /// <param name="input">原始输入。</param><param name="binding">期望匹配的配置。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("A", "shift+a")]
    [InlineData("\u001b[97:65;2u", "shift+a")]
    [InlineData("\u001b[27;2;65~", "shift+a")]
    [InlineData("\u001b[108;9u", "super+l")]
    [InlineData("\u001b[108;13u", "ctrl+super+l")]
    [InlineData("\u001b\u0018", "ctrl+alt+x")]
    [InlineData("\u001b[1081::113;5u", "ctrl+q")]
    [InlineData("\u001b[47:63;2u", "shift+/")]
    [InlineData("\u001b[97;65u", "a")]
    public async Task Editor_UsesRawIdentityForApplicationActions(string input, string binding)
    {
        var manager = new TuiKeybindingsManager(new Dictionary<string, TuiKeybindingDefinition> { ["test.action"] = new([binding]) });
        var reader = new TuiDecodedKeyReader(new RawReader(input));
        var editor = new InteractiveInputEditor(reader, new Renderer(), bindings: new TuiEditorKeyBindingMap(manager,
            [new("test.action", EditorAction.SelectModel)]));
        var result = await editor.ReadLineAsync("> ");
        Assert.Equal(EditorAction.SelectModel, result.Action); Assert.Equal(string.Empty, editor.GetExpandedDraft());
    }

    /// <summary>【Tui】【字符与修饰键】兼容控制台表示保留大写字符及 Shift，不影响普通字符输入。</summary>
    /// <param name="input">原始输入。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("A")]
    [InlineData("\u001b[97:65;2u")]
    [InlineData("\u001b[27;2;65~")]
    public async Task Decoder_RetainsShiftAndPrintableCharacter(string input)
    {
        var reader = new TuiDecodedKeyReader(new RawReader(input));
        var result = await reader.ReadInputEventAsync();
        Assert.Equal(input, result.RawInput); Assert.Equal('A', result.Key.KeyChar); Assert.Equal(ConsoleModifiers.Shift, result.Key.Modifiers);
    }

    /// <summary>【Tui】【未绑定修饰键】Super 不降级为普通字母，也不能触发没有 Super 的同键绑定。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task UnboundSuperKey_DoesNotInsertTextOrTriggerPlainBinding()
    {
        var manager = new TuiKeybindingsManager(userBindings: new Dictionary<string, IReadOnlyList<string>> { ["tui.input.submit"] = ["enter", "x"] });
        var editor = new InteractiveInputEditor(new TuiDecodedKeyReader(new RawReader("\u001b[120;9u", "y", "\r")), new Renderer(),
            bindings: new TuiEditorKeyBindingMap(manager));
        Assert.Equal("y", (await editor.ReadLineAsync("> ")).Text);
        Assert.False(TuiConsoleKeyInfoMapper.TryMapKeyId("super+x", null, out _));
    }

    /// <summary>【Tui】【释放事件】按键释放不能触发动作，后续真实按键仍可处理。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ReleasedSuperKey_IsIgnored()
    {
        var reader = new TuiDecodedKeyReader(new RawReader("\u001b[120;9:3u", "y"));
        var result = await reader.ReadInputEventAsync(); Assert.Equal("y", result.RawInput);
    }

    /// <summary>【Tui】【布局身份】数字和拉丁字母以逻辑键为准，不被物理布局位置劫持。</summary>
    /// <param name="codepoint">逻辑键码。</param><param name="correct">对应逻辑配置。</param>
    [Theory]
    [InlineData(53, "ctrl+5")]
    [InlineData(97, "ctrl+a")]
    public void LayoutFallback_DoesNotOverrideRecognizedDigitsOrLetters(int codepoint, string correct)
    {
        var raw = $"\u001b[{codepoint}::113;5u";
        Assert.True(TuiKeyDecoder.MatchesKey(raw, correct)); Assert.False(TuiKeyDecoder.MatchesKey(raw, "ctrl+q"));
    }

    /// <summary>【Tui】【Windows 标点】控制台成字结果中的隐含 Shift 不妨碍问号动作，Ctrl 和 Alt 仍精确区分。</summary>
    [Fact]
    public void ConsolePunctuation_MatchesProducedCharacterWithoutLosingControlModifiers()
    {
        var manager = new TuiKeybindingsManager(userBindings: new Dictionary<string, IReadOnlyList<string>> { ["tui.select.confirm"] = ["?"] });
        Assert.True(manager.Matches(new ConsoleKeyInfo('?', ConsoleKey.Oem2, true, false, false), "tui.select.confirm"));
        Assert.False(manager.Matches(new ConsoleKeyInfo('?', ConsoleKey.Oem2, true, true, false), "tui.select.confirm"));
        Assert.False(manager.Matches(new ConsoleKeyInfo('?', ConsoleKey.Oem2, true, false, true), "tui.select.confirm"));
        Assert.False(manager.Matches(new ConsoleKeyInfo('/', ConsoleKey.Oem2, false, false, false), "tui.select.confirm"));
    }

    /// <summary>【Tui】【原始输入夹具】依次提供完整终端事件，耗尽时直接失败。</summary><param name="inputs">原始事件。</param>
    private sealed class RawReader(params string[] inputs) : ITuiRawInputReader
    {
        private readonly Queue<string> _inputs = new(inputs);
        /// <summary>【Tui】【事件读取】返回下一个完整输入。</summary><param name="cancellationToken">取消信号。</param><returns>原始事件。</returns>
        public ValueTask<string> ReadInputAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_inputs.Dequeue()); }
    }

    /// <summary>【Tui】【输入渲染夹具】忽略输出，仅验证输入结果。</summary>
    private sealed class Renderer : IInteractiveRenderer
    {
        public int WindowWidth => 80;
        /// <summary>【Tui】【提示渲染】忽略提示。</summary><param name="prompt">提示。</param><param name="color">颜色。</param>
        public void WritePrompt(string prompt, ConsoleColor? color = null) { }
        /// <summary>【Tui】【文本渲染】忽略编辑帧。</summary><param name="buffer">文本。</param><param name="cursorIndex">光标。</param>
        public void Render(string buffer, int cursorIndex) { }
        /// <summary>【Tui】【搜索渲染】忽略搜索帧。</summary><param name="pattern">关键字。</param><param name="match">匹配。</param><param name="cursorInMatch">光标。</param>
        public void RenderSearch(string pattern, string? match, int cursorInMatch) { }
        /// <summary>【Tui】【提交渲染】忽略提交。</summary>
        public void Commit() { }
        /// <summary>【Tui】【取消渲染】忽略取消。</summary>
        public void Cancel() { }
    }
}
