// 作者：xxx
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed class TuiKeybindingsManagerTests
{
    /// <summary>【Tui】【动作匹配】原始终端输入支持默认导航、换行及 CSI-u 修饰键。</summary>
    /// <param name="input">终端输入。</param><param name="action">动作。</param><returns>无返回值。</returns>
    [Theory]
    [InlineData("\n", "tui.input.newLine")]
    [InlineData("\u001b[106;5u", "tui.input.newLine")]
    [InlineData("\u001b[1;5H", "tui.editor.cursorLineStart")]
    [InlineData("\u001b[6;5~", "tui.editor.pageDown")]
    [InlineData("\u001b[A", "tui.select.up")]
    [InlineData("\u001b[1;6A", "tui.altScreen.previousPrompt")]
    [InlineData("\u001b[1;5B", "tui.altScreen.nextPrompt")]
    [InlineData("\u001b[102;6u", "tui.altScreen.search")]
    public void Defaults_MatchTerminalProtocols(string input, string action) => Assert.True(new TuiKeybindingsManager().Matches(input, action));

    /// <summary>【Tui】【上下文共享】覆盖提交或选择动作不驱逐其他上下文的同键默认绑定。</summary>
    [Fact]
    public void Overrides_KeepSharedDefaultBindings()
    {
        var manager = new TuiKeybindingsManager(userBindings: TuiKeybindingsManager.ParseConfiguration("""
            {"tui.input.submit":["enter","ctrl+enter","enter"],"tui.select.up":["up","ctrl+p"]}
            """));
        Assert.Equal(["enter", "ctrl+enter"], manager.GetKeys("tui.input.submit"));
        Assert.Equal(["enter"], manager.GetKeys("tui.select.confirm"));
        Assert.Equal(["up"], manager.GetKeys("tui.editor.cursorUp"));
        Assert.Empty(manager.GetConflicts());
        Assert.Empty(manager.GetKeys("tui.editor.historyPrevious"));
        Assert.Empty(manager.GetKeys("unknown"));
        Assert.Null(manager.GetDefinition("unknown"));
    }

    /// <summary>【Tui】【冲突报告】只报告已注册动作之间精确相同的显式绑定，并保留输入顺序。</summary>
    [Fact]
    public void Conflicts_IgnoreDefaultsAndUnknownActions()
    {
        var manager = new TuiKeybindingsManager(userBindings: TuiKeybindingsManager.ParseConfiguration("""
            {"tui.input.submit":["ctrl+x","ctrl+x"],"tui.select.confirm":"ctrl+x","unknown":"ctrl+x","tui.select.cancel":"CTRL+X"}
            """));
        var conflict = Assert.Single(manager.GetConflicts());
        Assert.Equal("ctrl+x", conflict.Key);
        Assert.Equal(["tui.input.submit", "tui.select.confirm"], conflict.Keybindings);
        Assert.Equal(["left", "ctrl+b"], manager.GetKeys("tui.editor.cursorLeft"));
        Assert.Equal(["ctrl+x"], manager.GetUserBindings()["unknown"]);
        Assert.False(manager.GetResolvedBindings().ContainsKey("unknown"));
    }

    /// <summary>【Tui】【配置隔离】输入和输出数组的修改不能更改管理器，重新设置会整体替换旧覆盖。</summary>
    [Fact]
    public void Snapshots_AreIndependentAndReplacementRestoresDefaults()
    {
        string[] keys = ["ctrl+x"];
        var manager = new TuiKeybindingsManager(userBindings: new Dictionary<string, IReadOnlyList<string>> { ["tui.input.submit"] = keys });
        keys[0] = "escape";
        ((string[])manager.GetKeys("tui.input.submit"))[0] = "escape";
        ((string[])manager.GetUserBindings()["tui.input.submit"])[0] = "escape";
        ((string[])manager.GetResolvedBindings()["tui.input.submit"])[0] = "escape";
        ((string[])manager.GetDefinition("tui.input.submit")!.DefaultKeys)[0] = "escape";
        Assert.Equal(["ctrl+x"], manager.GetKeys("tui.input.submit"));
        manager.SetUserBindings(new Dictionary<string, IReadOnlyList<string>> { ["tui.select.confirm"] = [] });
        Assert.Equal(["enter"], manager.GetKeys("tui.input.submit"));
        Assert.Empty(manager.GetKeys("tui.select.confirm"));
    }

    /// <summary>【Tui】【配置校验】空数组禁用，混合类型数组和空值不覆盖默认动作。</summary>
    [Fact]
    public void Configuration_ValidatesValuesAndAcceptsBom()
    {
        var manager = new TuiKeybindingsManager(userBindings: TuiKeybindingsManager.ParseConfiguration("\ufeff" + """
            {"tui.select.confirm":[],"tui.select.up":["ctrl+p",1],"tui.select.down":null,"tui.input.submit":true}
            """));
        Assert.Empty(manager.GetKeys("tui.select.confirm"));
        Assert.Equal(["up"], manager.GetKeys("tui.select.up"));
        Assert.Equal(["down"], manager.GetKeys("tui.select.down"));
        Assert.Equal(["enter"], manager.GetKeys("tui.input.submit"));
        Assert.Empty(TuiKeybindingsManager.ParseConfiguration("[]"));
        Assert.Empty(TuiKeybindingsManager.ParseConfiguration("null"));
    }

    /// <summary>【Tui】【控制台修饰键】动作匹配保留精确修饰键，不把 Super 绑定降级到普通键。</summary>
    [Fact]
    public void ConsoleKeys_PreserveModifiersAndPrintableSymbols()
    {
        var manager = new TuiKeybindingsManager(userBindings: TuiKeybindingsManager.ParseConfiguration("""
            {"tui.select.up":"ctrl+p","tui.select.down":"super+p","tui.select.confirm":"?"}
            """));
        Assert.True(manager.Matches(new ConsoleKeyInfo('\u0010', ConsoleKey.P, false, false, true), "tui.select.up"));
        Assert.False(manager.Matches(new ConsoleKeyInfo('\u0010', ConsoleKey.P, true, false, true), "tui.select.up"));
        Assert.False(manager.Matches(new ConsoleKeyInfo('p', ConsoleKey.P, false, false, false), "tui.select.down"));
        Assert.True(manager.Matches(new ConsoleKeyInfo('?', ConsoleKey.Oem2, false, false, false), "tui.select.confirm"));
        Assert.False(manager.Matches(new ConsoleKeyInfo('/', ConsoleKey.Oem2, false, false, false), "tui.select.confirm"));
    }
}
