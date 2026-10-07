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
    Undo,
    Yank,
    YankPop,
    PromptHistoryPrevious,
    PromptHistoryNext,
    ClearEditor,
    Interrupt,
    ExitIfEmpty,
    CycleThinkingLevel,
    CopyLastMessage,
    NewSession,
    OpenSessionTree,
    ForkSession,
    ResumeSession,
    Ignore,
}

public readonly record struct KeyBinding(ConsoleKey Key, ConsoleModifiers Modifiers)
{
    public static KeyBinding From(ConsoleKeyInfo info) => new(info.Key, info.Modifiers);
}

public interface IKeyBindingMap
{
    /// <summary>是否保留旧版编辑器内置的撤销和取回快捷键；动作名配置可以显式禁用这些动作。</summary>
    bool UseLegacyShortcuts => true;
    IReadOnlyDictionary<KeyBinding, EditorAction> Bindings { get; }
    EditorAction Resolve(ConsoleKeyInfo key);
    /// <summary>【Tui】【输入上下文】按当前是否有文本解析按键，兼容映射默认不区分上下文。</summary>
    /// <param name="key">按键。</param><param name="hasText">输入区是否包含文本。</param><returns>编辑动作。</returns>
    EditorAction Resolve(ConsoleKeyInfo key, bool hasText) => Resolve(key);

    /// <summary>【Tui】【原始动作】按完整输入事件解析动作，旧映射保持控制台兼容。</summary>
    /// <param name="input">完整按键事件。</param><param name="hasText">草稿是否非空。</param><returns>编辑动作。</returns>
    EditorAction ResolveInput(ConsoleInputEvent input, bool hasText) => Resolve(input.Key, hasText);
}
