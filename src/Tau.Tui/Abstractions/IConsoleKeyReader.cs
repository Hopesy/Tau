namespace Tau.Tui.Abstractions;

public interface IConsoleKeyReader
{
    ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default);
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
