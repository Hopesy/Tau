using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public sealed class SystemConsoleTerminal : ITerminal
{
    public Task<string?> PromptAsync(
        string prompt,
        ConsoleColor? color = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Write(prompt, color);
        return Task.FromResult(Console.ReadLine());
    }

    public void Write(string text, ConsoleColor? color = null)
    {
        WithColor(color, () => Console.Write(text));
    }

    public void WriteLine(string? text = null, ConsoleColor? color = null)
    {
        WithColor(color, () => Console.WriteLine(text));
    }

    private static void WithColor(ConsoleColor? color, Action action)
    {
        var previous = Console.ForegroundColor;
        try
        {
            if (color is not null)
            {
                Console.ForegroundColor = color.Value;
            }

            action();
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }
}
