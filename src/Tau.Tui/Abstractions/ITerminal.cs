namespace Tau.Tui.Abstractions;

public interface ITerminal
{
    Task<string?> PromptAsync(string prompt, ConsoleColor? color = null, CancellationToken cancellationToken = default);
    void Write(string text, ConsoleColor? color = null);
    void WriteLine(string? text = null, ConsoleColor? color = null);
}
