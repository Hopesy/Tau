using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public interface ITuiRawInputReader
{
    ValueTask<string> ReadInputAsync(CancellationToken cancellationToken = default);
}

public sealed class TuiDecodedKeyReader(ITuiRawInputReader reader) : IConsoleKeyReader
{
    private readonly ITuiRawInputReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var input = await _reader.ReadInputAsync(cancellationToken).ConfigureAwait(false);
            if (TuiConsoleKeyInfoMapper.TryMapInput(input, out var key))
            {
                return key;
            }
        }
    }
}

public static class TuiConsoleKeyInfoMapper
{
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
            key = CreatePrintableKey(printable[0], ConsoleModifiers.None);
            return true;
        }

        var keyId = TuiKeyDecoder.ParseKey(input);
        return TryMapKeyId(keyId, input, out key);
    }

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
