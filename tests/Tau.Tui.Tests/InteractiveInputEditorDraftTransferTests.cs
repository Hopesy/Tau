// 作者：xxx
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed partial class InteractiveInputEditorTests
{
    /// <summary>【Tui】【应用光标交接】离开输入区执行应用动作后，在原光标处插入规范化文本并继续输入。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ApplicationInsertion_PreservesCursorAndNormalizesText()
    {
        var reader = new FakeInputEventReader(); reader.EnqueueKey(ConsoleKey.LeftArrow); reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.F2); reader.Enqueue('!'); reader.EnqueueKey(ConsoleKey.Enter);
        var bindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction> { [new(ConsoleKey.F2, default)] = EditorAction.PasteImage });
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), bindings: bindings);
        var session = new InteractiveConsoleSession(new FakeTerminal(), editor); session.SetDraft("abcd");
        Assert.Equal(EditorAction.PasteImage, (await session.ReadInputResultAsync()).Action);
        Assert.Equal(2, session.GetDraftCursorIndex());
        session.InsertTextAtCursor("中\r\n文\t");
        Assert.Equal("ab中\n文    cd", session.GetDraft()); Assert.Equal(9, session.GetDraftCursorIndex());
        Assert.Equal("ab中\n文    !cd", (await session.ReadInputResultAsync()).Text);
    }

    /// <summary>【Tui】【程序插入撤销】插入整段文本是单个撤销步骤，撤销后恢复光标并在原位置继续编辑。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ApplicationInsertion_IsAtomicForUndoAcrossActions()
    {
        var reader = new FakeInputEventReader(); reader.EnqueueKey(ConsoleKey.LeftArrow); reader.EnqueueKey(ConsoleKey.F2);
        reader.EnqueueKey(ConsoleKey.F3); reader.Enqueue('!'); reader.EnqueueKey(ConsoleKey.Enter);
        var bindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction>
            { [new(ConsoleKey.F2, default)] = EditorAction.PasteImage, [new(ConsoleKey.F3, default)] = EditorAction.Undo });
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), bindings: bindings); editor.SetExpandedDraft("ab");
        await editor.ReadLineAsync("> "); editor.InsertTextAtCursor("many\nlines");
        Assert.Equal("a!b", (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【设置草稿撤销】程序替换可以撤销，恢复折叠粘贴表后仍发送原始多行文本。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ProgrammaticReplacement_UndoRestoresPasteSnapshot()
    {
        var text = string.Join('\n', Enumerable.Repeat("original", 15));
        var reader = new FakeInputEventReader(); reader.EnqueuePaste(text); reader.EnqueueKey(ConsoleKey.F2); reader.EnqueueKey(ConsoleKey.F3); reader.EnqueueKey(ConsoleKey.Enter);
        var bindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction>
            { [new(ConsoleKey.F2, default)] = EditorAction.OpenExternalEditor, [new(ConsoleKey.F3, default)] = EditorAction.Undo });
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), bindings: bindings);
        await editor.ReadLineAsync("> "); Assert.Equal(text.Length, editor.GetExpandedCursorIndex());
        editor.SetExpandedDraft("replacement");
        Assert.Equal(text, (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【标记同形撤销】替换为与折叠标记相同的字面文本仍算内容变化，撤销必须恢复原粘贴。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ReplacementMatchingCollapsedMarker_RemainsUndoable()
    {
        var text = new string('x', 3000);
        var reader = new FakeInputEventReader(); reader.EnqueuePaste(text); reader.EnqueueKey(ConsoleKey.F2); reader.EnqueueKey(ConsoleKey.F3); reader.EnqueueKey(ConsoleKey.Enter);
        var bindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction>
            { [new(ConsoleKey.F2, default)] = EditorAction.OpenExternalEditor, [new(ConsoleKey.F3, default)] = EditorAction.Undo });
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), bindings: bindings);
        await editor.ReadLineAsync("> "); var marker = editor.GetCollapsedDraft(); editor.SetExpandedDraft(marker);
        Assert.Equal(marker, editor.GetExpandedDraft()); Assert.Equal(text, (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【动作连续撤销】应用动作不会清除已有撤销历史，但成功提交会清除上一条输入的历史。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ApplicationAction_PreservesUndoUntilSubmission()
    {
        var reader = new FakeInputEventReader(); reader.Enqueue('a'); reader.Enqueue('b'); reader.EnqueueKey(ConsoleKey.F2);
        reader.EnqueueKey(ConsoleKey.F3); reader.EnqueueKey(ConsoleKey.Enter); reader.EnqueueKey(ConsoleKey.F3); reader.EnqueueKey(ConsoleKey.Enter);
        var bindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction>
            { [new(ConsoleKey.F2, default)] = EditorAction.CopyLastMessage, [new(ConsoleKey.F3, default)] = EditorAction.Undo });
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), bindings: bindings);
        await editor.ReadLineAsync("> "); Assert.Equal("a", (await editor.ReadLineAsync("> ")).Text);
        Assert.Equal(string.Empty, (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【草稿转移边界】转出草稿不保留可撤回的历史，新的输入上下文不会恢复旧内容。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task TakeDraft_ClearsUndoAndCursorState()
    {
        var reader = new FakeInputEventReader(); reader.EnqueueKey(ConsoleKey.F3); reader.EnqueueKey(ConsoleKey.Enter);
        var bindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction> { [new(ConsoleKey.F3, default)] = EditorAction.Undo });
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), bindings: bindings);
        editor.SetExpandedDraft("before"); editor.InsertTextAtCursor("after"); Assert.Equal("beforeafter", editor.TakeDraft());
        Assert.Equal(0, editor.GetExpandedCursorIndex()); Assert.Equal(string.Empty, (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【应用动作草稿】应用动作读取完整粘贴，返回编辑后仍保留折叠内容并能完整提交。</summary>
    /// <param name="action">离开输入循环的应用动作。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(EditorAction.SelectModel)]
    [InlineData(EditorAction.OpenExternalEditor)]
    [InlineData(EditorAction.QueueFollowUpMessage)]
    [InlineData(EditorAction.CopyLastMessage)]
    public async Task ApplicationAction_RetainsExpandedPasteAcrossEditorReads(EditorAction action)
    {
        var text = string.Join('\n', Enumerable.Range(0, 15).Select(index => "中文 line " + index));
        var reader = new FakeInputEventReader(); reader.EnqueuePaste(text); reader.EnqueueKey(ConsoleKey.F2); reader.EnqueueKey(ConsoleKey.Enter);
        var bindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction> { [new(ConsoleKey.F2, default)] = action });
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), bindings: bindings);
        var session = new InteractiveConsoleSession(new FakeTerminal(), editor);
        var result = await session.ReadInputResultAsync();
        Assert.Equal(action, result.Action); Assert.Equal(text, session.GetDraft()); Assert.Contains("[paste #", editor.GetCollapsedDraft());
        Assert.Equal(text, (await session.ReadInputResultAsync()).Text); Assert.Equal(string.Empty, session.GetDraft());
    }

    /// <summary>【Tui】【外部草稿替换】外部编辑替换后的标记样式文字不再引用旧粘贴，也不影响下一轮输入。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task SessionSetDraft_ClearsPasteReferencesAndPreservesLiteralMarkers()
    {
        var reader = new FakeInputEventReader(); reader.EnqueuePaste(new string('x', 3000)); reader.EnqueueKey(ConsoleKey.F2); reader.EnqueueKey(ConsoleKey.Enter);
        var bindings = KeyBindingMap.WithOverrides(new Dictionary<KeyBinding, EditorAction> { [new(ConsoleKey.F2, default)] = EditorAction.OpenExternalEditor });
        var editor = new InteractiveInputEditor(reader, new FakeRenderer(), bindings: bindings);
        var session = new InteractiveConsoleSession(new FakeTerminal(), editor);
        await session.ReadInputResultAsync(); Assert.Contains("[paste #", editor.GetCollapsedDraft());
        const string replacement = "literal [paste #1 3000 chars]";
        session.SetDraft(replacement);
        Assert.Equal(replacement, session.GetDraft()); Assert.Equal(replacement, (await session.ReadInputResultAsync()).Text);
        session.SetDraft(null); Assert.Equal(string.Empty, session.GetDraft());
    }
}
