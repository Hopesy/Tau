// 作者：xxx
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed partial class InteractiveInputEditorTests
{
    /// <summary>【Tui】【命名撤销】撤销支持改键，旧的硬编码 Ctrl+Z 不再绕过当前动作配置。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task NamedKeys_UndoUsesConfiguredActionOnly()
    {
        var reader = new FakeKeyReader();
        reader.Enqueue('a'); reader.Enqueue('b'); reader.EnqueueKey(ConsoleKey.Backspace);
        reader.EnqueueKey(ConsoleKey.F2);
        reader.EnqueueKey(ConsoleKey.Backspace);
        reader.EnqueueRaw(new('\u001a', ConsoleKey.Z, false, false, true));
        reader.EnqueueKey(ConsoleKey.Enter);
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), bindings: NamedEditorBindings("""{"tui.editor.undo":"f2"}"""));
        Assert.Equal("a", (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【禁用撤销】空数组禁用所有默认撤销键，编辑器不会回退到硬编码快捷键。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task NamedKeys_EmptyArrayDisablesUndo()
    {
        var reader = new FakeKeyReader(); reader.Enqueue('a'); reader.Enqueue('b'); reader.EnqueueKey(ConsoleKey.Backspace);
        reader.EnqueueRaw(new('\u001f', ConsoleKey.OemMinus, false, false, true));
        reader.EnqueueRaw(new('\u001a', ConsoleKey.Z, false, false, true)); reader.EnqueueKey(ConsoleKey.Enter);
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), bindings: NamedEditorBindings("""{"tui.editor.undo":[]}"""));
        Assert.Equal("a", (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【命名取回】删除环支持改键及轮换，旧 Ctrl+Y 不会产生额外取回。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task NamedKeys_YankAndYankPopUseConfiguredActions()
    {
        var reader = new FakeKeyReader();
        foreach (var ch in "one") reader.Enqueue(ch);
        reader.EnqueueRaw(new('\u0015', ConsoleKey.U, false, false, true));
        foreach (var ch in "two") reader.Enqueue(ch);
        reader.EnqueueRaw(new('\u0015', ConsoleKey.U, false, false, true));
        reader.EnqueueRaw(new('\u0019', ConsoleKey.Y, false, false, true));
        reader.EnqueueKey(ConsoleKey.F2); reader.EnqueueKey(ConsoleKey.F3); reader.EnqueueKey(ConsoleKey.Enter);
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), bindings: NamedEditorBindings("""{"tui.editor.yank":"f2","tui.editor.yankPop":"f3"}"""));
        Assert.Equal("one", (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【空删除环】可打印字符绑定取回动作时，即使没有历史也不能作为普通字符输入。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task NamedKeys_EmptyYankConsumesPrintableBinding()
    {
        var reader = new FakeKeyReader(); reader.EnqueueRaw(new('q', ConsoleKey.Q, false, false, false)); reader.EnqueueKey(ConsoleKey.Enter);
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), bindings: NamedEditorBindings("""{"tui.editor.yank":"q"}"""));
        Assert.Equal("", (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【提示历史】专用历史动作可从多行草稿进入历史，普通向上移动仍只移动行内光标。</summary>
    /// <param name="historyAction">是否使用专用历史动作。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NamedKeys_HistoryIsIndependentOfCursorMovement(bool historyAction)
    {
        var reader = new FakeKeyReader();
        reader.EnqueueRaw(historyAction ? new('\u0010', ConsoleKey.P, false, false, true) : new('\0', ConsoleKey.UpArrow, false, false, false));
        reader.EnqueueKey(ConsoleKey.Enter);
        var history = new InputHistory(); history.Add("previous");
        var buffer = new InputBuffer(); buffer.SetDraft("two\nlines");
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), buffer, history,
            NamedEditorBindings("""{"tui.editor.historyPrevious":"ctrl+p"}"""));
        Assert.Equal(historyAction ? "previous" : "two\nlines", (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【配置重载】同一映射对象跟随动作管理器更新，选择器共享按键不抢占编辑器提交。</summary>
    [Fact]
    public void NamedKeys_MapTracksReloadAndContext()
    {
        var manager = new TuiKeybindingsManager(userBindings: TuiKeybindingsManager.ParseConfiguration("""{"tui.select.confirm":"f2"}"""));
        var map = new TuiEditorKeyBindingMap(manager);
        Assert.Equal(EditorAction.None, map.Resolve(new('\0', ConsoleKey.F2, false, false, false)));
        manager.SetUserBindings(TuiKeybindingsManager.ParseConfiguration("""{"tui.input.submit":"f2"}"""));
        Assert.Equal(EditorAction.Submit, map.Resolve(new('\0', ConsoleKey.F2, false, false, false)));
        Assert.Equal(EditorAction.None, map.Resolve(new('\r', ConsoleKey.Enter, false, false, false)));
        Assert.Equal(EditorAction.Submit, map.Bindings[new(ConsoleKey.F2, ConsoleModifiers.None)]);
    }

    /// <summary>【Tui】【命名键位夹具】为输入编辑器创建独立的动作配置。</summary>
    /// <param name="json">覆盖 JSON。</param><returns>命名动作映射。</returns>
    private static IKeyBindingMap NamedEditorBindings(string json) => new TuiEditorKeyBindingMap(new TuiKeybindingsManager(userBindings: TuiKeybindingsManager.ParseConfiguration(json)));
}
