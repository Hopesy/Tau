// 作者：xxx
using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

/// <summary>【Tui】【编辑器键位】将命名动作映射到编辑器操作，按输入处理优先级解决上下文内冲突。</summary>
public sealed class TuiEditorKeyBindingMap : IKeyBindingMap
{
    private readonly TuiKeybindingsManager _manager;
    private readonly IReadOnlyList<KeyValuePair<string, EditorAction>> _actions;
    private static readonly KeyValuePair<string, EditorAction>[] EditorActions =
    [
        new("tui.editor.undo", EditorAction.Undo),
        new("tui.input.tab", EditorAction.Complete),
        new("tui.editor.deleteToLineEnd", EditorAction.KillToLineEnd),
        new("tui.editor.deleteToLineStart", EditorAction.KillToLineStart),
        new("tui.editor.deleteWordBackward", EditorAction.DeletePrevWord),
        new("tui.editor.deleteWordForward", EditorAction.DeleteNextWord),
        new("tui.editor.deleteCharBackward", EditorAction.DeletePrevChar),
        new("tui.editor.deleteCharForward", EditorAction.DeleteNextChar),
        new("tui.editor.yank", EditorAction.Yank),
        new("tui.editor.yankPop", EditorAction.YankPop),
        new("tui.editor.historyPrevious", EditorAction.PromptHistoryPrevious),
        new("tui.editor.historyNext", EditorAction.PromptHistoryNext),
        new("tui.editor.cursorLineStart", EditorAction.CursorLineStart),
        new("tui.editor.cursorLineEnd", EditorAction.CursorLineEnd),
        new("tui.editor.cursorWordLeft", EditorAction.CursorPrevWord),
        new("tui.editor.cursorWordRight", EditorAction.CursorNextWord),
        new("tui.input.newLine", EditorAction.NewLine),
        new("tui.input.submit", EditorAction.Submit),
        new("tui.editor.cursorUp", EditorAction.HistoryPrev),
        new("tui.editor.cursorDown", EditorAction.HistoryNext),
        new("tui.editor.cursorRight", EditorAction.CursorRight),
        new("tui.editor.cursorLeft", EditorAction.CursorLeft)
    ];

    /// <summary>【Tui】【动作接线】应用动作优先于基础编辑动作，选择器动作不会占用输入区按键。</summary>
    /// <param name="manager">命名动作管理器。</param><param name="applicationActions">可选应用动作，按匹配优先级排列。</param>
    public TuiEditorKeyBindingMap(TuiKeybindingsManager manager, IReadOnlyList<KeyValuePair<string, EditorAction>>? applicationActions = null)
    {
        _manager = manager;
        _actions = (applicationActions ?? []).Concat(EditorActions).ToArray();
    }

    public bool UseLegacyShortcuts => false;

    /// <summary>【Tui】【提示键位】投影当前控制台可表示的按键，重复键保留优先匹配的动作。</summary>
    public IReadOnlyDictionary<KeyBinding, EditorAction> Bindings
    {
        get
        {
            var result = new Dictionary<KeyBinding, EditorAction>();
            foreach (var (id, action) in _actions)
                foreach (var key in _manager.GetKeys(id))
                    if (!key.ToLowerInvariant().Split('+').Contains("super") && TuiConsoleKeyInfoMapper.TryMapKeyId(key.ToLowerInvariant(), null, out var info))
                        result.TryAdd(KeyBinding.From(info), action);
            return result;
        }
    }

    /// <summary>【Tui】【动作解析】按当前配置匹配操作，空数组禁用和配置重载即时生效。</summary>
    /// <param name="key">当前控制台按键。</param><returns>匹配动作或 None。</returns>
    public EditorAction Resolve(ConsoleKeyInfo key) => Resolve(key, hasText: false);

    /// <summary>【Tui】【条件退出】有文本时跳过空输入退出动作，继续匹配编辑删除或普通字符输入。</summary>
    /// <param name="key">当前按键。</param><param name="hasText">是否有文本。</param><returns>当前上下文动作。</returns>
    public EditorAction Resolve(ConsoleKeyInfo key, bool hasText)
    {
        foreach (var (id, action) in _actions)
            if (!(hasText && action == EditorAction.ExitIfEmpty) && _manager.Matches(key, id)) return action;
        return EditorAction.None;
    }

    /// <summary>【Tui】【协议动作】优先通过原始终端协议匹配键位，无法表示的修饰键不得退化为普通动作。</summary>
    /// <param name="input">完整输入事件。</param><param name="hasText">草稿是否非空。</param><returns>当前上下文动作。</returns>
    public EditorAction ResolveInput(ConsoleInputEvent input, bool hasText)
    {
        if (input.RawInput is not { } raw) return Resolve(input.Key, hasText);
        foreach (var (id, action) in _actions)
            if (!(hasText && action == EditorAction.ExitIfEmpty) && _manager.Matches(raw, id)) return action;
        return EditorAction.None;
    }
}
