using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public sealed class TuiPassiveTerminal : ITerminal
{
    public Task<string?> PromptAsync(
        string prompt,
        ConsoleColor? color = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Console.ReadLine());
    }

    public void Write(string text, ConsoleColor? color = null)
    {
    }

    public void WriteLine(string? text = null, ConsoleColor? color = null)
    {
    }
}
