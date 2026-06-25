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
    public static ConsoleInputEvent KeyPress(ConsoleKeyInfo key) =>
        new(ConsoleInputEventKind.KeyPress, key, PasteText: null);

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
