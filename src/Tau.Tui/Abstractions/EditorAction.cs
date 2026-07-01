namespace Tau.Tui.Abstractions;

public enum EditorAction
{
    None,
    Cancel,
    Submit,
    NewLine,
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
    Complete,
    CompletePrevious,
    CycleModelForward,
    CycleModelBackward,
    SelectModel,
    PasteImage,
    ToggleThinkingBlock,
    ToggleToolOutputExpansion,
    OpenExternalEditor,
    QueueFollowUpMessage,
    RestoreQueuedMessages,
}

public readonly record struct KeyBinding(ConsoleKey Key, ConsoleModifiers Modifiers)
{
    public static KeyBinding From(ConsoleKeyInfo info) => new(info.Key, info.Modifiers);
}

public interface IKeyBindingMap
{
    IReadOnlyDictionary<KeyBinding, EditorAction> Bindings { get; }
    EditorAction Resolve(ConsoleKeyInfo key);
}
