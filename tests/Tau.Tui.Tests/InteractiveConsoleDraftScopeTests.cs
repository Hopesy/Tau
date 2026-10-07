// 作者：xxx
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed partial class InteractiveInputEditorTests
{
    /// <summary>【Tui】【草稿路由】活动输入作用域中的文本和光标操作只影响活动编辑器，释放后恢复主输入区。</summary>
    [Fact]
    public void DraftEditorScope_RoutesEditsAndRestoresPreviousEditor()
    {
        var main = new InteractiveInputEditor(new FakeKeyReader(), new FakeRenderer()); main.SetExpandedDraft("main");
        var active = new InteractiveInputEditor(new FakeKeyReader(), new FakeRenderer()); active.SetExpandedDraft("ab", 1);
        var ui = new InteractiveConsoleSession(new FakeTerminal(), main);
        var scope = ui.UseDraftEditor(active);
        Assert.Equal("ab", ui.GetDraft()); Assert.Equal(1, ui.GetDraftCursorIndex());
        ui.InsertTextAtCursor("中"); Assert.Equal("a中b", active.GetExpandedDraft()); Assert.Equal("main", main.GetExpandedDraft());
        scope.Dispose(); scope.Dispose(); Assert.Equal("main", ui.GetDraft()); Assert.Equal(4, ui.GetDraftCursorIndex());
    }

    /// <summary>【Tui】【嵌套草稿路由】内层作用域恢复外层编辑器，外层释放后恢复主编辑器。</summary>
    [Fact]
    public void NestedDraftEditorScopes_RestoreInOrder()
    {
        var main = new InteractiveInputEditor(new FakeKeyReader(), new FakeRenderer()); main.SetExpandedDraft("main");
        var first = new InteractiveInputEditor(new FakeKeyReader(), new FakeRenderer()); first.SetExpandedDraft("first");
        var second = new InteractiveInputEditor(new FakeKeyReader(), new FakeRenderer()); second.SetExpandedDraft("second");
        var ui = new InteractiveConsoleSession(new FakeTerminal(), main);
        using (ui.UseDraftEditor(first))
        {
            using (ui.UseDraftEditor(second)) { ui.SetDraft("changed", 2); Assert.Equal("changed", second.GetExpandedDraft()); Assert.Equal(2, ui.GetDraftCursorIndex()); }
            Assert.Equal("first", ui.GetDraft());
        }
        Assert.Equal("main", ui.GetDraft());
    }
}
