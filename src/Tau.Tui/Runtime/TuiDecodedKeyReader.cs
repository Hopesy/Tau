using System.Buffers;
using System.Text;
using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public interface ITuiRawInputReader
{
    ValueTask<string> ReadInputAsync(CancellationToken cancellationToken = default);
}

public sealed class TuiDecodedKeyReader(ITuiRawInputReader reader) : IConsoleInputEventReader, IDisposable
{
    private readonly ITuiRawInputReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly Queue<ConsoleKeyInfo> _legacyTextKeys = new();

    /// <summary>【Tui】【控制台兼容读取】旧接口逐个返回 UTF-16 字符，完整事件接口保持一次文本输入的原子性。</summary>
    /// <param name="cancellationToken">读取取消信号。</param><returns>下一个控制台字符或按键。</returns>
    public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_legacyTextKeys.TryDequeue(out var pending)) return pending;
        while (true)
        {
            var inputEvent = await ReadInputEventAsync(cancellationToken).ConfigureAwait(false);
            if (inputEvent.Kind == ConsoleInputEventKind.KeyPress)
            {
                if (inputEvent.Text is { Length: > 0 } text)
                {
                    foreach (var character in text) _legacyTextKeys.Enqueue(new(character, ConsoleKey.NoName, false, false, false));
                    return _legacyTextKeys.Dequeue();
                }
                return inputEvent.Key;
            }
        }
    }

    /// <summary>【Tui】【完整输入解码】保持粘贴、可打印 Unicode 文本及快捷键协议身份。</summary>
    /// <param name="cancellationToken">读取取消信号。</param><returns>下一个完整输入事件。</returns>
    public async ValueTask<ConsoleInputEvent> ReadInputEventAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var input = await _reader.ReadInputAsync(cancellationToken).ConfigureAwait(false);
            if (TryParseBracketedPaste(input, out var pasteText))
            {
                return ConsoleInputEvent.Paste(pasteText);
            }

            if (TuiConsoleKeyInfoMapper.TryMapInput(input, out var key))
            {
                return ConsoleInputEvent.KeyPress(key, input);
            }
            // 1. 【Tui】【Unicode 文本】非 BMP 字符和输入法一次提交的文本不能截断为单个 char
            if (!TuiKeyDecoder.IsKeyRelease(input))
            {
                var printable = TuiKeyDecoder.DecodePrintableKey(input) ?? input;
                if (IsPrintableText(printable)) return ConsoleInputEvent.TextInput(printable, input);
            }
            // 1. 【Tui】【完整修饰键】保留 ConsoleKeyInfo 无法表示的已识别协议，默认键值不会插入普通字符
            if (!TuiKeyDecoder.IsKeyRelease(input) && TuiKeyDecoder.ParseKey(input) is not null)
                return ConsoleInputEvent.KeyPress(default, input);
        }
    }

    /// <summary>【Tui】【文本校验】只接收完整 Unicode 标量，过滤控制序列和未配对代理字符。</summary>
    /// <param name="text">候选文本。</param><returns>是否全部可作为普通文本输入。</returns>
    private static bool IsPrintableText(string text)
    {
        if (text.Length == 0) return false;
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done || Rune.IsControl(rune)) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    internal static bool TryParseBracketedPaste(string input, out string text)
    {
        const string start = "\u001b[200~";
        const string end = "\u001b[201~";
        text = string.Empty;
        if (string.IsNullOrEmpty(input) ||
            !input.StartsWith(start, StringComparison.Ordinal) ||
            !input.EndsWith(end, StringComparison.Ordinal))
        {
            return false;
        }

        text = input[start.Length..^end.Length];
        return true;
    }

    public void Dispose()
    {
        if (_reader is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

public sealed class TuiStreamRawInputReader : ITuiRawInputReader, IDisposable
{
    private readonly Stream _input;
    private readonly TuiInputSequenceBuffer _sequenceBuffer;
    private readonly CancellationTokenSource _disposed = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly Queue<string> _queue = new();
    private readonly object _sync = new();
    private readonly Task _pumpTask;
    private bool _completed;
    private Exception? _error;

    public TuiStreamRawInputReader(
        Stream input,
        TimeSpan? sequenceTimeout = null,
        int bufferSize = 256)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
            throw new ArgumentException("Input stream must be readable.", nameof(input));
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));

        _input = input;
        _sequenceBuffer = new TuiInputSequenceBuffer(sequenceTimeout);
        _sequenceBuffer.Data += Enqueue;
        _sequenceBuffer.Paste += content => Enqueue($"\u001b[200~{content}\u001b[201~");
        _pumpTask = Task.Run(() => PumpAsync(bufferSize));
    }

    public async ValueTask<string> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_sync)
            {
                if (_queue.Count > 0)
                    return _queue.Dequeue();

                if (_error is not null)
                    throw new IOException("Raw TUI input reader failed.", _error);

                if (_completed)
                    throw new EndOfStreamException("Raw TUI input stream has ended.");
            }

            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed.IsCancellationRequested)
            return;

        _disposed.Cancel();
        _sequenceBuffer.Dispose();
        _available.Release();
    }

    /// <summary>【Tui】【UTF-8 输入流】跨字节读取保留解码状态，再把完整文本交给终端序列分帧。</summary>
    /// <param name="bufferSize">每次读取的最大字节数。</param><returns>输入流泵任务。</returns>
    private async Task PumpAsync(int bufferSize)
    {
        var buffer = new byte[bufferSize];
        var decoder = Encoding.UTF8.GetDecoder();
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bufferSize)];
        try
        {
            while (!_disposed.IsCancellationRequested)
            {
                var read = await _input.ReadAsync(buffer, _disposed.Token).ConfigureAwait(false);
                if (read == 0)
                    break;

                // 1. 【Tui】【字节分包】中文和表情可跨任意字节边界，不能对每个分包独立解码
                var count = decoder.GetChars(buffer.AsSpan(0, read), characters, flush: false);
                if (count > 0) _sequenceBuffer.Process(new string(characters, 0, count));
            }

            var remaining = decoder.GetChars(ReadOnlySpan<byte>.Empty, characters, flush: true);
            if (remaining > 0) _sequenceBuffer.Process(new string(characters, 0, remaining));

            foreach (var pending in _sequenceBuffer.Flush())
            {
                Enqueue(pending);
            }

            Complete(null);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            Complete(null);
        }
        catch (ObjectDisposedException) when (_disposed.IsCancellationRequested)
        {
            Complete(null);
        }
        catch (Exception ex)
        {
            Complete(ex);
        }
    }

    private void Enqueue(string input)
    {
        lock (_sync)
        {
            _queue.Enqueue(input);
        }

        _available.Release();
    }

    private void Complete(Exception? error)
    {
        lock (_sync)
        {
            _error = error;
            _completed = true;
        }

        _available.Release();
    }
}

public static class TuiConsoleKeyInfoMapper
{
    /// <summary>【Tui】【控制台兼容映射】保留可打印字符和可表示的修饰键，无法表示的按键交由原始事件处理。</summary>
    /// <param name="input">原始终端输入。</param><param name="key">控制台按键。</param><returns>是否能表示。</returns>
    public static bool TryMapInput(string input, out ConsoleKeyInfo key)
    {
        key = default;
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        if (TuiKeyDecoder.IsKeyRelease(input))
        {
            TuiKeyDecoder.ParseKey(input);
            return false;
        }

        var printable = TuiKeyDecoder.DecodePrintableKey(input);
        if (!string.IsNullOrEmpty(printable))
        {
            if (printable.Length != 1 || char.IsSurrogate(printable[0])) return false;
            var identity = TuiKeyDecoder.ParseKey(input);
            var modifiers = TryMapKeyId(identity, input, out var mapped) ? mapped.Modifiers : ConsoleModifiers.None;
            key = CreatePrintableKey(printable[0], modifiers);
            return true;
        }

        // 【终端输入】【Unicode 映射】非控制字符（包括中文）不是键盘协议序列，直接作为普通字符交给输入编辑器
        if (input.Length == 1 && !char.IsControl(input[0]) && !char.IsSurrogate(input[0]))
        {
            key = CreatePrintableKey(input[0], input[0] is >= 'A' and <= 'Z' ? ConsoleModifiers.Shift : ConsoleModifiers.None);
            return true;
        }

        var keyId = TuiKeyDecoder.ParseKey(input);
        return TryMapKeyId(keyId, input, out key);
    }

    /// <summary>【Tui】【键名映射】把可表示的命名键转换为控制台按键，不忽略未知或 Super 修饰键。</summary>
    /// <param name="keyId">按键名称。</param><param name="rawInput">兼容调用方提供的原始输入。</param>
    /// <param name="key">映射结果。</param><returns>是否能表示。</returns>
    public static bool TryMapKeyId(string? keyId, string? rawInput, out ConsoleKeyInfo key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(keyId))
        {
            return false;
        }

        var parts = keyId.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var baseKey = parts[^1];
        var modifiers = ConsoleModifiers.None;
        foreach (var modifier in parts[..^1])
        {
            if (modifier.ToLowerInvariant() is not ("shift" or "alt" or "ctrl")) return false;
            modifiers |= modifier.ToLowerInvariant() switch
            {
                "shift" => ConsoleModifiers.Shift,
                "alt" => ConsoleModifiers.Alt,
                "ctrl" => ConsoleModifiers.Control,
                _ => ConsoleModifiers.None
            };
        }

        key = baseKey switch
        {
            "escape" or "esc" => CreateControlKey('\u001b', ConsoleKey.Escape, modifiers),
            "enter" or "return" => CreateControlKey('\r', ConsoleKey.Enter, modifiers),
            "tab" => CreateControlKey('\t', ConsoleKey.Tab, modifiers),
            "space" => CreatePrintableKey(' ', modifiers),
            "backspace" => CreateControlKey('\b', ConsoleKey.Backspace, modifiers),
            "delete" => CreateControlKey('\0', ConsoleKey.Delete, modifiers),
            "insert" => CreateControlKey('\0', ConsoleKey.Insert, modifiers),
            "home" => CreateControlKey('\0', ConsoleKey.Home, modifiers),
            "end" => CreateControlKey('\0', ConsoleKey.End, modifiers),
            "pageUp" or "pageup" => CreateControlKey('\0', ConsoleKey.PageUp, modifiers),
            "pageDown" or "pagedown" => CreateControlKey('\0', ConsoleKey.PageDown, modifiers),
            "up" => CreateControlKey('\0', ConsoleKey.UpArrow, modifiers),
            "down" => CreateControlKey('\0', ConsoleKey.DownArrow, modifiers),
            "left" => CreateControlKey('\0', ConsoleKey.LeftArrow, modifiers),
            "right" => CreateControlKey('\0', ConsoleKey.RightArrow, modifiers),
            "clear" => CreateControlKey('\0', ConsoleKey.Clear, modifiers),
            "f1" => CreateControlKey('\0', ConsoleKey.F1, modifiers),
            "f2" => CreateControlKey('\0', ConsoleKey.F2, modifiers),
            "f3" => CreateControlKey('\0', ConsoleKey.F3, modifiers),
            "f4" => CreateControlKey('\0', ConsoleKey.F4, modifiers),
            "f5" => CreateControlKey('\0', ConsoleKey.F5, modifiers),
            "f6" => CreateControlKey('\0', ConsoleKey.F6, modifiers),
            "f7" => CreateControlKey('\0', ConsoleKey.F7, modifiers),
            "f8" => CreateControlKey('\0', ConsoleKey.F8, modifiers),
            "f9" => CreateControlKey('\0', ConsoleKey.F9, modifiers),
            "f10" => CreateControlKey('\0', ConsoleKey.F10, modifiers),
            "f11" => CreateControlKey('\0', ConsoleKey.F11, modifiers),
            "f12" => CreateControlKey('\0', ConsoleKey.F12, modifiers),
            _ when baseKey.Length == 1 => CreateCharacterKey(baseKey[0], modifiers),
            _ => default
        };

        return key != default || string.Equals(keyId, "ctrl+space", StringComparison.OrdinalIgnoreCase);
    }

    private static ConsoleKeyInfo CreateCharacterKey(char character, ConsoleModifiers modifiers)
    {
        if ((modifiers & ConsoleModifiers.Control) != 0 &&
            TryGetControlCharacter(character, out var controlCharacter, out var controlKey))
        {
            return CreateControlKey(controlCharacter, controlKey, modifiers);
        }

        return CreatePrintableKey(character, modifiers);
    }

    private static ConsoleKeyInfo CreatePrintableKey(char character, ConsoleModifiers modifiers)
    {
        var key = ToConsoleKey(character);
        return new ConsoleKeyInfo(
            character,
            key,
            (modifiers & ConsoleModifiers.Shift) != 0,
            (modifiers & ConsoleModifiers.Alt) != 0,
            (modifiers & ConsoleModifiers.Control) != 0);
    }

    private static ConsoleKeyInfo CreateControlKey(char keyChar, ConsoleKey consoleKey, ConsoleModifiers modifiers) =>
        new(
            keyChar,
            consoleKey,
            (modifiers & ConsoleModifiers.Shift) != 0,
            (modifiers & ConsoleModifiers.Alt) != 0,
            (modifiers & ConsoleModifiers.Control) != 0);

    private static ConsoleKey ToConsoleKey(char character)
    {
        var upper = char.ToUpperInvariant(character);
        if (upper is >= 'A' and <= 'Z')
        {
            return (ConsoleKey)((int)ConsoleKey.A + upper - 'A');
        }

        if (character is >= '0' and <= '9')
        {
            return (ConsoleKey)((int)ConsoleKey.D0 + character - '0');
        }

        return character switch
        {
            ' ' => ConsoleKey.Spacebar,
            '-' or '_' => ConsoleKey.OemMinus,
            '=' or '+' => ConsoleKey.OemPlus,
            '[' or '{' => ConsoleKey.Oem4,
            ']' or '}' => ConsoleKey.Oem6,
            '\\' or '|' => ConsoleKey.Oem5,
            ';' or ':' => ConsoleKey.Oem1,
            '\'' or '"' => ConsoleKey.Oem7,
            ',' or '<' => ConsoleKey.OemComma,
            '.' or '>' => ConsoleKey.OemPeriod,
            '/' or '?' => ConsoleKey.Oem2,
            '`' or '~' => ConsoleKey.Oem3,
            _ => ConsoleKey.NoName
        };
    }

    private static bool TryGetControlCharacter(char character, out char controlCharacter, out ConsoleKey controlKey)
    {
        var lower = char.ToLowerInvariant(character);
        if (lower is >= 'a' and <= 'z')
        {
            controlCharacter = (char)(lower & 0x1f);
            controlKey = ToConsoleKey(lower);
            return true;
        }

        (controlCharacter, controlKey) = lower switch
        {
            ' ' => ('\0', ConsoleKey.Spacebar),
            '[' => ('\u001b', ConsoleKey.Oem4),
            '\\' => ('\u001c', ConsoleKey.Oem5),
            ']' => ('\u001d', ConsoleKey.Oem6),
            '-' => ('\u001f', ConsoleKey.OemMinus),
            _ => ('\0', ConsoleKey.NoName)
        };
        return controlKey != ConsoleKey.NoName;
    }
}
