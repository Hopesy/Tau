namespace Tau.Tui.Abstractions;

public enum EditorAction
{
    None,
    Cancel,
    Submit,
    DeletePrevChar,
    DeletePrevWord,
    DeleteNextChar,
    DeleteNextWord,
    CursorLeft,
    CursorRight,
    CursorPrevWord,
    CursorNextWord,
    CursorLineStart,
    CursorLineEnd,
    KillToLineStart,
    KillToLineEnd,
    HistoryPrev,
    HistoryNext,
    ReverseSearch,
}

public readonly record struct KeyBinding(ConsoleKey Key, ConsoleModifiers Modifiers)
{
    public static KeyBinding From(ConsoleKeyInfo info) => new(info.Key, info.Modifiers);
}

public interface IKeyBindingMap
{
    EditorAction Resolve(ConsoleKeyInfo key);
}
