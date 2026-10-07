// 作者：xxx
namespace Tau.Tui.Runtime;

/// <summary>【Tui】【快捷键定义】保存上游终端动作的默认按键和描述，动作间允许共享按键。</summary>
public static class TuiKeybindingDefinitions
{
    /// <summary>全部终端默认动作，使用动作标识的精确大小写。</summary>
    public static IReadOnlyDictionary<string, TuiKeybindingDefinition> All { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, TuiKeybindingDefinition>(new Dictionary<string, TuiKeybindingDefinition>(StringComparer.Ordinal)
    {
        ["tui.editor.cursorUp"] = new(Array.AsReadOnly<string>(["up"]), "Move cursor up"),
        ["tui.editor.cursorDown"] = new(Array.AsReadOnly<string>(["down"]), "Move cursor down"),
        ["tui.editor.historyPrevious"] = new(Array.AsReadOnly<string>([]), "Select previous prompt history entry"),
        ["tui.editor.historyNext"] = new(Array.AsReadOnly<string>([]), "Select next prompt history entry"),
        ["tui.editor.cursorLeft"] = new(Array.AsReadOnly<string>(["left", "ctrl+b"]), "Move cursor left"),
        ["tui.editor.cursorRight"] = new(Array.AsReadOnly<string>(["right", "ctrl+f"]), "Move cursor right"),
        ["tui.editor.cursorWordLeft"] = new(Array.AsReadOnly<string>(["alt+left", "ctrl+left", "alt+b"]), "Move cursor word left"),
        ["tui.editor.cursorWordRight"] = new(Array.AsReadOnly<string>(["alt+right", "ctrl+right", "alt+f"]), "Move cursor word right"),
        ["tui.editor.cursorLineStart"] = new(Array.AsReadOnly<string>(["home", "ctrl+home", "ctrl+a"]), "Move to line start"),
        ["tui.editor.cursorLineEnd"] = new(Array.AsReadOnly<string>(["end", "ctrl+end", "ctrl+e"]), "Move to line end"),
        ["tui.editor.jumpForward"] = new(Array.AsReadOnly<string>(["ctrl+]"]), "Jump forward to character"),
        ["tui.editor.jumpBackward"] = new(Array.AsReadOnly<string>(["ctrl+alt+]"]), "Jump backward to character"),
        ["tui.editor.pageUp"] = new(Array.AsReadOnly<string>(["pageUp", "ctrl+pageUp"]), "Page up"),
        ["tui.editor.pageDown"] = new(Array.AsReadOnly<string>(["pageDown", "ctrl+pageDown"]), "Page down"),
        ["tui.editor.deleteCharBackward"] = new(Array.AsReadOnly<string>(["backspace"]), "Delete character backward"),
        ["tui.editor.deleteCharForward"] = new(Array.AsReadOnly<string>(["delete", "ctrl+d"]), "Delete character forward"),
        ["tui.editor.deleteWordBackward"] = new(Array.AsReadOnly<string>(["ctrl+w", "alt+backspace"]), "Delete word backward"),
        ["tui.editor.deleteWordForward"] = new(Array.AsReadOnly<string>(["alt+d", "alt+delete"]), "Delete word forward"),
        ["tui.editor.deleteToLineStart"] = new(Array.AsReadOnly<string>(["ctrl+u"]), "Delete to line start"),
        ["tui.editor.deleteToLineEnd"] = new(Array.AsReadOnly<string>(["ctrl+k"]), "Delete to line end"),
        ["tui.editor.yank"] = new(Array.AsReadOnly<string>(["ctrl+y"]), "Yank"),
        ["tui.editor.yankPop"] = new(Array.AsReadOnly<string>(["alt+y"]), "Yank pop"),
        ["tui.editor.undo"] = new(Array.AsReadOnly<string>(["ctrl+-"]), "Undo"),
        ["tui.input.newLine"] = new(Array.AsReadOnly<string>(["shift+enter", "ctrl+j"]), "Insert newline"),
        ["tui.input.submit"] = new(Array.AsReadOnly<string>(["enter"]), "Submit input"),
        ["tui.input.tab"] = new(Array.AsReadOnly<string>(["tab"]), "Tab / autocomplete"),
        ["tui.input.copy"] = new(Array.AsReadOnly<string>(["ctrl+c"]), "Copy selection"),
        ["tui.select.up"] = new(Array.AsReadOnly<string>(["up"]), "Move selection up"),
        ["tui.select.down"] = new(Array.AsReadOnly<string>(["down"]), "Move selection down"),
        ["tui.select.pageUp"] = new(Array.AsReadOnly<string>(["pageUp"]), "Selection page up"),
        ["tui.select.pageDown"] = new(Array.AsReadOnly<string>(["pageDown"]), "Selection page down"),
        ["tui.select.confirm"] = new(Array.AsReadOnly<string>(["enter"]), "Confirm selection"),
        ["tui.select.cancel"] = new(Array.AsReadOnly<string>(["escape", "ctrl+c"]), "Cancel selection"),
        ["tui.altScreen.pageUp"] = new(Array.AsReadOnly<string>(["pageUp"]), "Scroll viewport up one page"),
        ["tui.altScreen.pageDown"] = new(Array.AsReadOnly<string>(["pageDown"]), "Scroll viewport down one page"),
        ["tui.altScreen.halfPageUp"] = new(Array.AsReadOnly<string>([]), "Scroll viewport up half a page"),
        ["tui.altScreen.halfPageDown"] = new(Array.AsReadOnly<string>([]), "Scroll viewport down half a page"),
        ["tui.altScreen.lineUp"] = new(Array.AsReadOnly<string>([]), "Scroll viewport up one line"),
        ["tui.altScreen.lineDown"] = new(Array.AsReadOnly<string>([]), "Scroll viewport down one line"),
        ["tui.altScreen.previousPrompt"] = new(Array.AsReadOnly<string>(["ctrl+shift+up", "ctrl+up"]), "Jump to previous semantic prompt"),
        ["tui.altScreen.nextPrompt"] = new(Array.AsReadOnly<string>(["ctrl+shift+down", "ctrl+down"]), "Jump to next semantic prompt"),
        ["tui.altScreen.search"] = new(Array.AsReadOnly<string>(["ctrl+shift+f"]), "Search the primary scroll view"),
        ["tui.altScreen.searchNext"] = new(Array.AsReadOnly<string>(["enter", "ctrl+g"]), "Select the next search match"),
        ["tui.altScreen.searchPrevious"] = new(Array.AsReadOnly<string>(["shift+enter", "ctrl+shift+g"]), "Select the previous search match"),
        ["tui.altScreen.searchClose"] = new(Array.AsReadOnly<string>(["escape"]), "Close transcript search"),
        ["tui.altScreen.top"] = new(Array.AsReadOnly<string>(["home"]), "Scroll viewport to top"),
        ["tui.altScreen.bottom"] = new(Array.AsReadOnly<string>(["end"]), "Scroll viewport to bottom"),
    });
}
