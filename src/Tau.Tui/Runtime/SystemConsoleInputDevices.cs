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
    private readonly Func<bool> _keyAvailable = static () => Console.KeyAvailable;
    private readonly Func<ConsoleKeyInfo> _readKey = static () => Console.ReadKey(intercept: true);

    /// <summary>【TUI】【控制台按键】注入非阻塞就绪检查，便于验证 Windows 默认输入取消。</summary>
    /// <param name="keyAvailable">按键是否就绪。</param><param name="readKey">读取已就绪按键。</param>
    internal SystemConsoleKeyReader(Func<bool> keyAvailable, Func<ConsoleKeyInfo> readKey)
    { _keyAvailable = keyAvailable; _readKey = readKey; }

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

        return ReadConsoleKeyAsync(cancellationToken);
    }

    /// <summary>【TUI】【控制台取消】只在有按键时调用同步读取，等待期间响应外部提示取消。</summary>
    /// <param name="token">取消信号。</param><returns>一个按键。</returns>
    private async ValueTask<ConsoleKeyInfo> ReadConsoleKeyAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_keyAvailable()) { token.ThrowIfCancellationRequested(); return _readKey(); }
            await Task.Delay(20, token).ConfigureAwait(false);
        }
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
        var configuredValue = Environment.GetEnvironmentVariable(RawInputEnvironmentVariable);
        // 【终端输入】【Unicode 兼容】Windows Console.ReadKey 能直接返回 IME 提交的 Unicode 字符，默认采用它与 Cade 保持一致；需要 VT 原始协议时可显式设置 TAU_TUI_RAW_INPUT=1
        if (OperatingSystem.IsWindows() &&
            string.IsNullOrWhiteSpace(configuredValue) &&
            !SafeIsInputRedirected() &&
            !SafeIsOutputRedirected())
        {
            return (null, null);
        }

        if (!ShouldUseRawInput(
                configuredValue,
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
    private bool _continueCurrentLine;
    private int _promptLength;
    private int _lastRenderedLength;
    private int _renderStartTop;
    private int _lastRenderedLineCount = 1;
    private int _lastRenderedFinalColumn;

    /// <summary>【TUI】【输入重绘】可选择从已经输出的提示末尾开始首次编辑，避免覆盖宿主提示文本。</summary>
    /// <param name="continueCurrentLine">首次渲染是否保留当前行已有内容。</param>
    public SystemConsoleInteractiveRenderer(bool continueCurrentLine = false) => _continueCurrentLine = continueCurrentLine;

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
            if (_continueCurrentLine)
            {
                try { _promptLength = Console.CursorLeft; }
                catch { }
                _lastRenderedFinalColumn = _promptLength;
                _continueCurrentLine = false;
            }
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
