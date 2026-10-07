namespace Tau.Tui.Abstractions;

public interface IConsoleKeyReader
{
    ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default);
}

public interface IConsoleInputEventReader : IConsoleKeyReader
{
    ValueTask<ConsoleInputEvent> ReadInputEventAsync(CancellationToken cancellationToken = default);
}

public readonly record struct ConsoleInputEvent(ConsoleInputEventKind Kind, ConsoleKeyInfo Key, string? PasteText)
{
    /// <summary>【Tui】【原始按键】保存协议身份与完整修饰键，避免控制台类型丢失 Super 或布局信息。</summary>
    public string? RawInput { get; init; }

    /// <summary>【Tui】【完整文本】保存单个终端事件产生的完整文本，包括 UTF-16 代理对和输入法组合。</summary>
    public string? Text { get; init; }

    /// <summary>【Tui】【文本事件】创建不能由单个控制台字符表示的输入，保留原始协议供键位匹配。</summary>
    /// <param name="text">完整可打印文本。</param><param name="rawInput">原始终端输入。</param><returns>文本按键事件。</returns>
    public static ConsoleInputEvent TextInput(string text, string? rawInput = null) =>
        KeyPress(default, rawInput) with { Text = text };

    /// <summary>【Tui】【按键事件】创建可选携带原始协议的按键事件。</summary>
    /// <param name="key">控制台兼容表示。</param><param name="rawInput">原始终端输入。</param><returns>按键事件。</returns>
    public static ConsoleInputEvent KeyPress(ConsoleKeyInfo key, string? rawInput = null) =>
        new(ConsoleInputEventKind.KeyPress, key, PasteText: null) { RawInput = rawInput };

    public static ConsoleInputEvent Paste(string text) =>
        new(ConsoleInputEventKind.Paste, default, text ?? string.Empty);
}

public enum ConsoleInputEventKind
{
    KeyPress,
    Paste,
}

public interface IInteractiveRenderer
{
    int WindowWidth { get; }
    void WritePrompt(string prompt, ConsoleColor? color = null);
    void Render(string buffer, int cursorIndex);
    void RenderSearch(string pattern, string? match, int cursorInMatch);
    void Commit();
    void Cancel();
}
