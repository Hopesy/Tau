// 作者：xxx
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed partial class InteractiveInputEditorTests
{
    /// <summary>【Tui】【阶段读取恢复】临时输入阶段使用指定读取器和键位，结束后恢复主输入且保留编辑状态。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task InputStage_RestoresReaderAndBindingsAfterAction()
    {
        var main = new FakeKeyReader(); main.Enqueue('!'); main.EnqueueKey(ConsoleKey.Enter);
        var stage = new FakeKeyReader(); stage.Enqueue('x'); stage.EnqueueKey(ConsoleKey.F2);
        var editor = new InteractiveInputEditor(main, new FakeRenderer());
        var result = await editor.ReadLineWithInputAsync(stage, NamedEditorBindings("""{"tui.input.submit":"f2"}"""), "> ");
        Assert.Equal("x", result.Text); Assert.Same(KeyBindingMap.Default, editor.KeyBindings);
        Assert.Equal("!", (await editor.ReadLineAsync("> ")).Text); Assert.Equal("x", editor.History.Peek(1));
    }

    /// <summary>【Tui】【阶段失败恢复】异常或预取消释放读取覆盖，不污染后续主输入。</summary>
    /// <param name="cancel">是否通过预取消结束。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputStage_RestoresReaderAfterFailure(bool cancel)
    {
        var main = new FakeKeyReader(); main.Enqueue('x'); main.EnqueueKey(ConsoleKey.Enter);
        var editor = new InteractiveInputEditor(main, new FakeRenderer());
        var task = editor.ReadLineWithInputAsync(new FakeKeyReader(), NamedEditorBindings("""{"tui.input.submit":"f2"}"""), "> ",
            cancellationToken: cancel ? new CancellationToken(true) : CancellationToken.None);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("x", (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【询问状态隔离】活动编辑器中的模态回答不改写草稿、光标、撤销记录和输入历史。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ModalPrompt_PreservesDraftCursorUndoAndHistory()
    {
        var reader = new FakeKeyReader(); reader.Enqueue('n'); reader.EnqueueKey(ConsoleKey.Enter);
        reader.EnqueueRaw(new('\u001f', ConsoleKey.OemMinus, false, false, true)); reader.EnqueueKey(ConsoleKey.Enter);
        var editor = new InteractiveInputEditor(reader, new FakeRenderer()); editor.SetExpandedDraft("ab", 1); editor.InsertTextAtCursor("X");
        var ui = new InteractiveConsoleSession(new FakeTerminal(), editor);
        using (ui.UseDraftEditor(editor))
        {
            Assert.Equal("n", await ui.ReadInputAsync("question> ", null));
            Assert.Equal("aXb", ui.GetDraft()); Assert.Equal(2, ui.GetDraftCursorIndex()); Assert.Equal(0, editor.History.Count);
        }
        Assert.Equal("ab", (await editor.ReadLineAsync("> ")).Text);
    }
}
