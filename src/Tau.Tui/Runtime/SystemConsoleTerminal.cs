using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public sealed class SystemConsoleTerminal : ITerminal
{
    /// <summary>【TUI】【终端提示】显示提示并等待可取消的编辑输入，支持重定向 stdin。</summary>
    /// <param name="prompt">提示。</param><param name="color">颜色。</param><param name="cancellationToken">取消信号。</param><returns>文本或 EOF。</returns>
    public Task<string?> PromptAsync(
        string prompt,
        ConsoleColor? color = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Write(prompt, color);
        return TuiConsoleInput.ReadLineAsync(token: cancellationToken);
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
