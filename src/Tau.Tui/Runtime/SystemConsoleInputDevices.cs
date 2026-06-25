using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public interface ITuiConsoleRawModeController
{
    IDisposable EnterRawMode();
}

public sealed class SystemConsoleKeyReader : IConsoleKeyReader, IDisposable
{
    private const string RawInputEnvironmentVariable = "TAU_TUI_RAW_INPUT";

    private readonly IConsoleKeyReader? _rawReader;
    private readonly IDisposable? _rawMode;

    public SystemConsoleKeyReader()
    {
        var rawInput = CreateEnvironmentRawInput();
        _rawReader = rawInput.RawReader;
        _rawMode = rawInput.RawMode;
    }

    private SystemConsoleKeyReader(IConsoleKeyReader? rawReader, IDisposable? rawMode = null)
    {
        _rawReader = rawReader;
        _rawMode = rawMode;
    }

    public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
    {
        if (_rawReader is not null)
        {
            return _rawReader.ReadKeyAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Console.ReadKey(intercept: true));
    }

    public static SystemConsoleKeyReader CreateRaw(
        Stream input,
        TimeSpan? sequenceTimeout = null,
        ITuiConsoleRawModeController? rawModeController = null)
    {
        var rawMode = rawModeController?.EnterRawMode();
        return new SystemConsoleKeyReader(
            new TuiDecodedKeyReader(new TuiStreamRawInputReader(input, sequenceTimeout)),
            rawMode);
    }

    internal static bool ShouldUseRawInput(
        string? configuredValue,
        bool isInputRedirected,
        bool isOutputRedirected)
    {
        var normalized = configuredValue?.Trim();
        if (string.IsNullOrEmpty(normalized))
            return !isInputRedirected && !isOutputRedirected;

        if (IsEnabledRawInputValue(normalized))
            return true;

        if (IsDisabledRawInputValue(normalized))
            return false;

        return !isInputRedirected && !isOutputRedirected;
    }

    private static (IConsoleKeyReader? RawReader, IDisposable? RawMode) CreateEnvironmentRawInput()
    {
        if (!ShouldUseRawInput(
                Environment.GetEnvironmentVariable(RawInputEnvironmentVariable),
                SafeIsInputRedirected(),
                SafeIsOutputRedirected()))
        {
            return (null, null);
        }

        var rawMode = new SystemTuiConsoleRawModeController().EnterRawMode();
        return (
            new TuiDecodedKeyReader(new TuiStreamRawInputReader(Console.OpenStandardInput())),
            rawMode);
    }

    private static bool IsEnabledRawInputValue(string value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static bool IsDisabledRawInputValue(string value) =>
        string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);

    private static bool SafeIsInputRedirected()
    {
        try
        {
            return Console.IsInputRedirected;
        }
        catch
        {
            return true;
        }
    }

    private static bool SafeIsOutputRedirected()
    {
        try
        {
            return Console.IsOutputRedirected;
        }
        catch
        {
            return true;
        }
    }

    public void Dispose()
    {
        if (_rawReader is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _rawMode?.Dispose();
    }
}

public sealed class SystemTuiConsoleRawModeController : ITuiConsoleRawModeController
{
    public IDisposable EnterRawMode() => TuiConsoleRawMode.Enter();
}

public sealed class SystemConsoleInteractiveRenderer : IInteractiveRenderer
{
    private int _promptLength;
    private int _lastRenderedLength;
    private int _renderStartTop;
    private int _lastRenderedLineCount = 1;
    private int _lastRenderedFinalColumn;

    public int WindowWidth => SafeGetWindowWidth();

    public void WritePrompt(string prompt, ConsoleColor? color = null)
    {
        _promptLength = prompt.Length;
        _lastRenderedLength = 0;
        _lastRenderedLineCount = 1;
        _lastRenderedFinalColumn = prompt.Length;
        var previous = Console.ForegroundColor;
        try
        {
            if (color is not null)
            {
                Console.ForegroundColor = color.Value;
            }

            Console.Write(prompt);
            _renderStartTop = SafeGetCursorTop();
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }

    public void Render(string buffer, int cursorIndex)
    {
        var rendered = buffer ?? string.Empty;
        var lines = SplitLines(rendered);
        var cursor = ComputeCursorPosition(rendered, cursorIndex);

        try
        {
            Console.CursorVisible = false;
        }
        catch
        {
            // Some terminals (CI hosts) don't support CursorVisible; ignore.
        }

        try
        {
            Console.SetCursorPosition(_promptLength, _renderStartTop);
        }
        catch
        {
            // No-op when running without a real console (tests, redirected output).
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                try
                {
                    Console.SetCursorPosition(0, _renderStartTop + i);
                }
                catch
                {
                    // ignore
                }
            }

            Console.Write(lines[i]);
            try
            {
                ClearRestOfLine();
            }
            catch
            {
                // ignore
            }
        }

        for (var i = lines.Length; i < _lastRenderedLineCount; i++)
        {
            try
            {
                Console.SetCursorPosition(0, _renderStartTop + i);
                ClearRestOfLine();
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            var cursorLeft = cursor.Line == 0 ? _promptLength + cursor.Column : cursor.Column;
            Console.SetCursorPosition(cursorLeft, _renderStartTop + cursor.Line);
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
        _lastRenderedLineCount = Math.Max(1, lines.Length);
        _lastRenderedFinalColumn = lines.Length == 1 ? _promptLength + lines[^1].Length : lines[^1].Length;
    }

    public void Commit()
    {
        MoveToRenderEnd();
        Console.WriteLine();
        _promptLength = 0;
        _lastRenderedLength = 0;
        _lastRenderedLineCount = 1;
        _renderStartTop = 0;
        _lastRenderedFinalColumn = 0;
    }

    public void Cancel()
    {
        MoveToRenderEnd();
        Console.WriteLine();
        _promptLength = 0;
        _lastRenderedLength = 0;
        _lastRenderedLineCount = 1;
        _renderStartTop = 0;
        _lastRenderedFinalColumn = 0;
    }

    public void RenderSearch(string pattern, string? match, int cursorInMatch)
    {
        var prefix = $"(reverse-i-search) `{pattern ?? string.Empty}': ";
        var rendered = match ?? string.Empty;

        try
        {
            Console.CursorVisible = false;
        }
        catch
        {
            // ignore
        }

        try
        {
            Console.SetCursorPosition(0, Console.CursorTop);
        }
        catch
        {
            // ignore
        }

        Console.Write(prefix);
        Console.Write(rendered);

        var written = prefix.Length + rendered.Length;
        if (written < _promptLength + _lastRenderedLength)
        {
            Console.Write(new string(' ', _promptLength + _lastRenderedLength - written));
        }

        try
        {
            Console.SetCursorPosition(prefix.Length + Math.Clamp(cursorInMatch, 0, rendered.Length), Console.CursorTop);
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

        _promptLength = prefix.Length;
        _lastRenderedLength = rendered.Length;
        _lastRenderedLineCount = 1;
        _renderStartTop = SafeGetCursorTop();
        _lastRenderedFinalColumn = prefix.Length + rendered.Length;
    }

    private static string[] SplitLines(string text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return normalized.Split('\n');
    }

    private static (int Line, int Column) ComputeCursorPosition(string text, int cursorIndex)
    {
        text ??= string.Empty;
        cursorIndex = Math.Clamp(cursorIndex, 0, text.Length);
        var line = 0;
        var column = 0;
        for (var i = 0; i < cursorIndex; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 0;
            }
            else if (text[i] != '\r')
            {
                column++;
            }
        }

        return (line, column);
    }

    private static void ClearRestOfLine()
    {
        try
        {
            Console.Write("\u001b[K");
        }
        catch
        {
            // ignore
        }
    }

    private void MoveToRenderEnd()
    {
        try
        {
            Console.SetCursorPosition(_lastRenderedFinalColumn, _renderStartTop + _lastRenderedLineCount - 1);
        }
        catch
        {
            // ignore
        }
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

    private static int SafeGetCursorTop()
    {
        try
        {
            return Console.CursorTop;
        }
        catch
        {
            return 0;
        }
    }
}
