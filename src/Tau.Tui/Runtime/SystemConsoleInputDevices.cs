using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public sealed class SystemConsoleKeyReader : IConsoleKeyReader
{
    public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Console.ReadKey(intercept: true));
    }
}

public sealed class SystemConsoleInteractiveRenderer : IInteractiveRenderer
{
    private int _promptLength;
    private int _lastRenderedLength;

    public int WindowWidth => SafeGetWindowWidth();

    public void WritePrompt(string prompt, ConsoleColor? color = null)
    {
        _promptLength = prompt.Length;
        _lastRenderedLength = 0;
        var previous = Console.ForegroundColor;
        try
        {
            if (color is not null)
            {
                Console.ForegroundColor = color.Value;
            }

            Console.Write(prompt);
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }

    public void Render(string buffer, int cursorIndex)
    {
        var rendered = buffer ?? string.Empty;

        try
        {
            Console.CursorVisible = false;
        }
        catch
        {
            // Some terminals (CI hosts) don't support CursorVisible; ignore.
        }

        // Re-paint by moving the cursor back to the column after the prompt
        // and overwriting any trailing characters that may have shrunk.
        try
        {
            Console.SetCursorPosition(_promptLength, Console.CursorTop);
        }
        catch
        {
            // No-op when running without a real console (tests, redirected output).
        }

        Console.Write(rendered);
        if (rendered.Length < _lastRenderedLength)
        {
            var padding = new string(' ', _lastRenderedLength - rendered.Length);
            Console.Write(padding);
            try
            {
                Console.SetCursorPosition(_promptLength + rendered.Length, Console.CursorTop);
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            Console.SetCursorPosition(_promptLength + cursorIndex, Console.CursorTop);
        }
        catch
        {
            // ignore
        }

        try
        {
            Console.CursorVisible = true;
        }
        catch
        {
            // ignore
        }

        _lastRenderedLength = rendered.Length;
    }

    public void Commit()
    {
        Console.WriteLine();
        _promptLength = 0;
        _lastRenderedLength = 0;
    }

    public void Cancel()
    {
        Console.WriteLine();
        _promptLength = 0;
        _lastRenderedLength = 0;
    }

    private static int SafeGetWindowWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch
        {
            return 80;
        }
    }
}
