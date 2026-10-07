// 作者：xxx
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Tests;

public sealed class TuiToolExecutionRendererTests
{
    /// <summary>【Tui】【工具正文扩展】宿主渲染器接收实际宽度及最新状态，绕过纯文本 Panel 以保留 ANSI 和统一行宽。</summary>
    [Fact]
    public void ToolBodyRendererReceivesLatestStateAndPreservesWidth()
    {
        TuiToolExecutionRenderContext? received = null;
        var panelCalls = 0;
        var component = new TuiToolExecution("custom", "call", "args", new(PanelRenderer: (_, _, _) => { panelCalls++; return []; }),
            showImages: false, bodyRenderer: context => { received = context; return ["\u001b[32mcustom body\u001b[39m"]; });
        component.SetExpandKeyHint("Ctrl+O"); component.SetExpanded(true);
        var result = new TuiToolExecutionResult([new TuiToolTextBlock("raw output")], Details: "details");
        component.UpdateResult(result);
        var lines = component.Render(24);
        Assert.NotNull(received); Assert.Equal(22, received.Width); Assert.Equal("args", received.Args);
        Assert.Same(result, received.Result); Assert.True(received.Expanded); Assert.False(received.IsPartial); Assert.False(received.ShowImages);
        Assert.Equal("Ctrl+O to expand", received.ExpandHint); Assert.Equal(0, panelCalls);
        Assert.All(lines, line => Assert.Equal(24, TuiText.VisibleWidth(line)));
        Assert.Contains("custom body", string.Join("\n", lines)); Assert.DoesNotContain("raw output", string.Join("\n", lines));
        component.UpdateArgs("changed"); component.UpdateResult(result, true); component.Render(10);
        Assert.Equal("changed", received.Args); Assert.Equal(8, received.Width); Assert.True(received.IsPartial);
    }
}
