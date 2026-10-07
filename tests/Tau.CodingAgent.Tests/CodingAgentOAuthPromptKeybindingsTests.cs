// 作者：xxx
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentOAuthPromptSelectorTests
{
    /// <summary>【CodingAgent】【方式快捷键】覆盖方向、确认和取消后不再接受原默认按键，提示同步显示覆盖键。</summary>
    [Fact]
    public void Component_UsesConfiguredActionsAndHints()
    {
        var bindings = new CodingAgentKeybindings(CodingAgentKeybindings.ParseUserConfiguration("""
            {"tui.select.up":"ctrl+p","tui.select.down":"ctrl+n","tui.select.confirm":"ctrl+x","tui.select.cancel":"ctrl+q"}
            """));
        var component = new CodingAgentOAuthPromptSelector.Component("Select", Options, keybindings: bindings);
        Assert.Equal(TuiInputResult.Ignored, component.HandleInput(Key(ConsoleKey.DownArrow)));
        component.HandleInput(new('\u000e', ConsoleKey.N, false, false, true)); Assert.Equal(1, component.SelectedIndex);
        component.HandleInput(new('\u0010', ConsoleKey.P, false, false, true)); Assert.Equal(0, component.SelectedIndex);
        Assert.Equal(TuiInputResult.Ignored, component.HandleInput(Key(ConsoleKey.Enter)));
        Assert.Equal(TuiInputResult.Ignored, component.HandleInput(Key(ConsoleKey.Escape)));
        var rendered = string.Join('\n', component.Render(80));
        Assert.Contains("ctrl+x select", rendered); Assert.Contains("ctrl+q cancel", rendered);
        component.HandleInput(new('\u0018', ConsoleKey.X, false, false, true)); Assert.Equal("browser", component.Selection);
        var cancelled = new CodingAgentOAuthPromptSelector.Component("Select", Options, keybindings: bindings);
        cancelled.HandleInput(new('\u0011', ConsoleKey.Q, false, false, true)); Assert.True(cancelled.Cancelled);
    }

    /// <summary>【CodingAgent】【动作优先级】工具展开优先于选择，导航优先于确认，保留 j/k 与换行兼容输入。</summary>
    [Fact]
    public void Component_PreservesPriorityAndDirectInputFallbacks()
    {
        var expanded = 0;
        var bindings = new CodingAgentKeybindings(CodingAgentKeybindings.ParseUserConfiguration("""
            {"app.tools.expand":"ctrl+x","tui.select.confirm":["ctrl+x","down"],"tui.select.cancel":[]}
            """));
        var component = new CodingAgentOAuthPromptSelector.Component("Select", Options, keybindings: bindings, onToggleToolsExpanded: () => expanded++);
        component.HandleInput(new('\u0018', ConsoleKey.X, false, false, true)); Assert.Equal(1, expanded); Assert.Null(component.Selection);
        component.HandleInput(Key(ConsoleKey.DownArrow)); Assert.Equal(1, component.SelectedIndex); Assert.Null(component.Selection);
        component.HandleInput(new('k', ConsoleKey.K, false, false, false)); Assert.Equal(0, component.SelectedIndex);
        component.HandleInput(new('j', ConsoleKey.J, false, false, false)); Assert.Equal(1, component.SelectedIndex);
        Assert.Equal(TuiInputResult.Ignored, component.HandleInput(Key(ConsoleKey.Escape)));
        component.HandleInput(new('\n', ConsoleKey.J, false, false, true)); Assert.Equal("device", component.Selection);
    }

    /// <summary>【CodingAgent】【组合面板】每次打开读取当前配置，覆盖层关闭后恢复焦点。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task CompositionSelector_UsesCurrentConfiguration()
    {
        var keys = new Keys([new('\u000e', ConsoleKey.N, false, false, true), new('\u0018', ConsoleKey.X, false, false, true)]);
        var session = new TuiCompositionSession(new Surface(), keys);
        var loaded = 0;
        var selector = CodingAgentOAuthPromptSelector.CreateCompositionSelector(session, keybindings: () =>
        {
            loaded++;
            return new CodingAgentKeybindings(CodingAgentKeybindings.ParseUserConfiguration("""{"tui.select.down":"ctrl+n","tui.select.confirm":"ctrl+x"}"""));
        });
        Assert.Equal("device", await selector("Select", Options, default));
        Assert.Equal(1, loaded); Assert.False(session.HasVisibleOverlay); Assert.False(session.HasFocusedInputOverlay);
    }
}
