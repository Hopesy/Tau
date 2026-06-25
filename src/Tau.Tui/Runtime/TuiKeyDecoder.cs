using System.Text.RegularExpressions;

namespace Tau.Tui.Runtime;

public enum TuiKeyEventType
{
    Press,
    Repeat,
    Release
}

public static class TuiKeyDecoder
{
    private const int ShiftModifier = 1;
    private const int AltModifier = 2;
    private const int CtrlModifier = 4;
    private const int SuperModifier = 8;
    private const int LockMask = 64 + 128;

    private const int EscapeCodepoint = 27;
    private const int TabCodepoint = 9;
    private const int EnterCodepoint = 13;
    private const int SpaceCodepoint = 32;
    private const int BackspaceCodepoint = 127;
    private const int KeypadEnterCodepoint = 57414;

    private const int ArrowUpCodepoint = -1;
    private const int ArrowDownCodepoint = -2;
    private const int ArrowRightCodepoint = -3;
    private const int ArrowLeftCodepoint = -4;

    private const int DeleteCodepoint = -10;
    private const int InsertCodepoint = -11;
    private const int PageUpCodepoint = -12;
    private const int PageDownCodepoint = -13;
    private const int HomeCodepoint = -14;
    private const int EndCodepoint = -15;

    private static readonly Regex KittyCsiURegex =
        new("^\u001b\\[(\\d+)(?::(\\d*))?(?::(\\d+))?(?:;(\\d+))?(?::(\\d+))?u$", RegexOptions.CultureInvariant);

    private static readonly Regex KittyArrowRegex =
        new("^\u001b\\[1;(\\d+)(?::(\\d+))?([ABCD])$", RegexOptions.CultureInvariant);

    private static readonly Regex KittyFunctionalRegex =
        new("^\u001b\\[(\\d+)(?:;(\\d+))?(?::(\\d+))?~$", RegexOptions.CultureInvariant);

    private static readonly Regex KittyHomeEndRegex =
        new("^\u001b\\[1;(\\d+)(?::(\\d+))?([HF])$", RegexOptions.CultureInvariant);

    private static readonly Regex ModifyOtherKeysRegex =
        new("^\u001b\\[27;(\\d+);(\\d+)~$", RegexOptions.CultureInvariant);

    private static readonly HashSet<char> SymbolKeys =
    [
        '`', '-', '=', '[', ']', '\\', ';', '\'', ',', '.', '/',
        '!', '@', '#', '$', '%', '^', '&', '*', '(', ')', '_',
        '+', '|', '~', '{', '}', ':', '<', '>', '?'
    ];

    private static readonly Dictionary<int, int> KittyFunctionalKeyEquivalents = new()
    {
        [57399] = '0',
        [57400] = '1',
        [57401] = '2',
        [57402] = '3',
        [57403] = '4',
        [57404] = '5',
        [57405] = '6',
        [57406] = '7',
        [57407] = '8',
        [57408] = '9',
        [57409] = '.',
        [57410] = '/',
        [57411] = '*',
        [57412] = '-',
        [57413] = '+',
        [57415] = '=',
        [57416] = ',',
        [57417] = ArrowLeftCodepoint,
        [57418] = ArrowRightCodepoint,
        [57419] = ArrowUpCodepoint,
        [57420] = ArrowDownCodepoint,
        [57421] = PageUpCodepoint,
        [57422] = PageDownCodepoint,
        [57423] = HomeCodepoint,
        [57424] = EndCodepoint,
        [57425] = InsertCodepoint,
        [57426] = DeleteCodepoint,
    };

    private static readonly Dictionary<string, string[]> LegacyKeySequences =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["up"] = ["\u001b[A", "\u001bOA"],
            ["down"] = ["\u001b[B", "\u001bOB"],
            ["right"] = ["\u001b[C", "\u001bOC"],
            ["left"] = ["\u001b[D", "\u001bOD"],
            ["home"] = ["\u001b[H", "\u001bOH", "\u001b[1~", "\u001b[7~"],
            ["end"] = ["\u001b[F", "\u001bOF", "\u001b[4~", "\u001b[8~"],
            ["insert"] = ["\u001b[2~"],
            ["delete"] = ["\u001b[3~"],
            ["pageUp"] = ["\u001b[5~", "\u001b[[5~"],
            ["pageDown"] = ["\u001b[6~", "\u001b[[6~"],
            ["clear"] = ["\u001b[E", "\u001bOE"],
            ["f1"] = ["\u001bOP", "\u001b[11~", "\u001b[[A"],
            ["f2"] = ["\u001bOQ", "\u001b[12~", "\u001b[[B"],
            ["f3"] = ["\u001bOR", "\u001b[13~", "\u001b[[C"],
            ["f4"] = ["\u001bOS", "\u001b[14~", "\u001b[[D"],
            ["f5"] = ["\u001b[15~", "\u001b[[E"],
            ["f6"] = ["\u001b[17~"],
            ["f7"] = ["\u001b[18~"],
            ["f8"] = ["\u001b[19~"],
            ["f9"] = ["\u001b[20~"],
            ["f10"] = ["\u001b[21~"],
            ["f11"] = ["\u001b[23~"],
            ["f12"] = ["\u001b[24~"],
        };

    private static readonly Dictionary<string, string[]> LegacyShiftSequences =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["up"] = ["\u001b[a"],
            ["down"] = ["\u001b[b"],
            ["right"] = ["\u001b[c"],
            ["left"] = ["\u001b[d"],
            ["clear"] = ["\u001b[e"],
            ["insert"] = ["\u001b[2$"],
            ["delete"] = ["\u001b[3$"],
            ["pageUp"] = ["\u001b[5$"],
            ["pageDown"] = ["\u001b[6$"],
            ["home"] = ["\u001b[7$"],
            ["end"] = ["\u001b[8$"],
        };

    private static readonly Dictionary<string, string[]> LegacyCtrlSequences =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["up"] = ["\u001bOa"],
            ["down"] = ["\u001bOb"],
            ["right"] = ["\u001bOc"],
            ["left"] = ["\u001bOd"],
            ["clear"] = ["\u001bOe"],
            ["insert"] = ["\u001b[2^"],
            ["delete"] = ["\u001b[3^"],
            ["pageUp"] = ["\u001b[5^"],
            ["pageDown"] = ["\u001b[6^"],
            ["home"] = ["\u001b[7^"],
            ["end"] = ["\u001b[8^"],
        };

    private static readonly Dictionary<string, string> LegacySequenceKeyIds =
        new(StringComparer.Ordinal)
        {
            ["\u001bOA"] = "up",
            ["\u001bOB"] = "down",
            ["\u001bOC"] = "right",
            ["\u001bOD"] = "left",
            ["\u001bOH"] = "home",
            ["\u001bOF"] = "end",
            ["\u001b[E"] = "clear",
            ["\u001bOE"] = "clear",
            ["\u001bOe"] = "ctrl+clear",
            ["\u001b[e"] = "shift+clear",
            ["\u001b[2~"] = "insert",
            ["\u001b[2$"] = "shift+insert",
            ["\u001b[2^"] = "ctrl+insert",
            ["\u001b[3$"] = "shift+delete",
            ["\u001b[3^"] = "ctrl+delete",
            ["\u001b[[5~"] = "pageUp",
            ["\u001b[[6~"] = "pageDown",
            ["\u001b[a"] = "shift+up",
            ["\u001b[b"] = "shift+down",
            ["\u001b[c"] = "shift+right",
            ["\u001b[d"] = "shift+left",
            ["\u001bOa"] = "ctrl+up",
            ["\u001bOb"] = "ctrl+down",
            ["\u001bOc"] = "ctrl+right",
            ["\u001bOd"] = "ctrl+left",
            ["\u001b[5$"] = "shift+pageUp",
            ["\u001b[6$"] = "shift+pageDown",
            ["\u001b[7$"] = "shift+home",
            ["\u001b[8$"] = "shift+end",
            ["\u001b[5^"] = "ctrl+pageUp",
            ["\u001b[6^"] = "ctrl+pageDown",
            ["\u001b[7^"] = "ctrl+home",
            ["\u001b[8^"] = "ctrl+end",
            ["\u001bOP"] = "f1",
            ["\u001bOQ"] = "f2",
            ["\u001bOR"] = "f3",
            ["\u001bOS"] = "f4",
            ["\u001b[11~"] = "f1",
            ["\u001b[12~"] = "f2",
            ["\u001b[13~"] = "f3",
            ["\u001b[14~"] = "f4",
            ["\u001b[[A"] = "f1",
            ["\u001b[[B"] = "f2",
            ["\u001b[[C"] = "f3",
            ["\u001b[[D"] = "f4",
            ["\u001b[[E"] = "f5",
            ["\u001b[15~"] = "f5",
            ["\u001b[17~"] = "f6",
            ["\u001b[18~"] = "f7",
            ["\u001b[19~"] = "f8",
            ["\u001b[20~"] = "f9",
            ["\u001b[21~"] = "f10",
            ["\u001b[23~"] = "f11",
            ["\u001b[24~"] = "f12",
            ["\u001bb"] = "alt+left",
            ["\u001bf"] = "alt+right",
            ["\u001bp"] = "alt+up",
            ["\u001bn"] = "alt+down",
        };

    private static bool _kittyProtocolActive;
    private static TuiKeyEventType _lastEventType = TuiKeyEventType.Press;

    public static void SetKittyProtocolActive(bool active) => _kittyProtocolActive = active;

    public static bool IsKittyProtocolActive() => _kittyProtocolActive;

    public static TuiKeyEventType LastEventType => _lastEventType;

    public static bool IsKeyRelease(string data)
    {
        data ??= string.Empty;
        if (data.Contains("\u001b[200~", StringComparison.Ordinal))
        {
            return false;
        }

        return data.Contains(":3u", StringComparison.Ordinal) ||
            data.Contains(":3~", StringComparison.Ordinal) ||
            data.Contains(":3A", StringComparison.Ordinal) ||
            data.Contains(":3B", StringComparison.Ordinal) ||
            data.Contains(":3C", StringComparison.Ordinal) ||
            data.Contains(":3D", StringComparison.Ordinal) ||
            data.Contains(":3H", StringComparison.Ordinal) ||
            data.Contains(":3F", StringComparison.Ordinal);
    }

    public static bool IsKeyRepeat(string data)
    {
        data ??= string.Empty;
        if (data.Contains("\u001b[200~", StringComparison.Ordinal))
        {
            return false;
        }

        return data.Contains(":2u", StringComparison.Ordinal) ||
            data.Contains(":2~", StringComparison.Ordinal) ||
            data.Contains(":2A", StringComparison.Ordinal) ||
            data.Contains(":2B", StringComparison.Ordinal) ||
            data.Contains(":2C", StringComparison.Ordinal) ||
            data.Contains(":2D", StringComparison.Ordinal) ||
            data.Contains(":2H", StringComparison.Ordinal) ||
            data.Contains(":2F", StringComparison.Ordinal);
    }

    public static bool MatchesKey(string data, string keyId)
    {
        if (!TryParseKeyId(keyId, out var key, out var modifier))
        {
            return false;
        }

        switch (key)
        {
            case "escape":
            case "esc":
                return modifier == 0 &&
                    (data == "\u001b" ||
                        MatchesKittySequence(data, EscapeCodepoint, 0) ||
                        MatchesModifyOtherKeys(data, EscapeCodepoint, 0));

            case "space":
                if (!_kittyProtocolActive)
                {
                    if (modifier == CtrlModifier && data == "\0")
                    {
                        return true;
                    }

                    if (modifier == AltModifier && data == "\u001b ")
                    {
                        return true;
                    }
                }

                return modifier == 0
                    ? data == " " || MatchesKittySequence(data, SpaceCodepoint, 0) || MatchesModifyOtherKeys(data, SpaceCodepoint, 0)
                    : MatchesKittySequence(data, SpaceCodepoint, modifier) || MatchesModifyOtherKeys(data, SpaceCodepoint, modifier);

            case "tab":
                if (modifier == ShiftModifier)
                {
                    return data == "\u001b[Z" ||
                        MatchesKittySequence(data, TabCodepoint, ShiftModifier) ||
                        MatchesModifyOtherKeys(data, TabCodepoint, ShiftModifier);
                }

                return modifier == 0
                    ? data == "\t" || MatchesKittySequence(data, TabCodepoint, 0)
                    : MatchesKittySequence(data, TabCodepoint, modifier) || MatchesModifyOtherKeys(data, TabCodepoint, modifier);

            case "enter":
            case "return":
                return MatchesEnter(data, modifier);

            case "backspace":
                return MatchesBackspace(data, modifier);

            case "insert":
                return MatchesFunctionalKey(data, "insert", InsertCodepoint, modifier);

            case "delete":
                return MatchesFunctionalKey(data, "delete", DeleteCodepoint, modifier);

            case "clear":
                return modifier == 0
                    ? MatchesLegacySequence(data, "clear")
                    : MatchesLegacyModifierSequence(data, "clear", modifier);

            case "home":
                return MatchesFunctionalKey(data, "home", HomeCodepoint, modifier);

            case "end":
                return MatchesFunctionalKey(data, "end", EndCodepoint, modifier);

            case "pageup":
                return MatchesFunctionalKey(data, "pageUp", PageUpCodepoint, modifier);

            case "pagedown":
                return MatchesFunctionalKey(data, "pageDown", PageDownCodepoint, modifier);

            case "up":
                if (modifier == AltModifier)
                {
                    return data == "\u001bp" || MatchesKittySequence(data, ArrowUpCodepoint, AltModifier);
                }

                return MatchesDirectionalKey(data, "up", ArrowUpCodepoint, modifier);

            case "down":
                if (modifier == AltModifier)
                {
                    return data == "\u001bn" || MatchesKittySequence(data, ArrowDownCodepoint, AltModifier);
                }

                return MatchesDirectionalKey(data, "down", ArrowDownCodepoint, modifier);

            case "left":
                if (modifier == AltModifier)
                {
                    return data == "\u001b[1;3D" ||
                        (!_kittyProtocolActive && data == "\u001bB") ||
                        data == "\u001bb" ||
                        MatchesKittySequence(data, ArrowLeftCodepoint, AltModifier);
                }

                if (modifier == CtrlModifier)
                {
                    return data == "\u001b[1;5D" ||
                        MatchesLegacyModifierSequence(data, "left", CtrlModifier) ||
                        MatchesKittySequence(data, ArrowLeftCodepoint, CtrlModifier);
                }

                return MatchesDirectionalKey(data, "left", ArrowLeftCodepoint, modifier);

            case "right":
                if (modifier == AltModifier)
                {
                    return data == "\u001b[1;3C" ||
                        (!_kittyProtocolActive && data == "\u001bF") ||
                        data == "\u001bf" ||
                        MatchesKittySequence(data, ArrowRightCodepoint, AltModifier);
                }

                if (modifier == CtrlModifier)
                {
                    return data == "\u001b[1;5C" ||
                        MatchesLegacyModifierSequence(data, "right", CtrlModifier) ||
                        MatchesKittySequence(data, ArrowRightCodepoint, CtrlModifier);
                }

                return MatchesDirectionalKey(data, "right", ArrowRightCodepoint, modifier);

            case "f1":
            case "f2":
            case "f3":
            case "f4":
            case "f5":
            case "f6":
            case "f7":
            case "f8":
            case "f9":
            case "f10":
            case "f11":
            case "f12":
                return modifier == 0 && MatchesLegacySequence(data, key);
        }

        return MatchesPrintableKey(data, key, modifier);
    }

    public static string? ParseKey(string data)
    {
        if (TryParseKittySequence(data, out var kitty))
        {
            return FormatParsedKey(kitty.Codepoint, kitty.Modifier, kitty.BaseLayoutKey);
        }

        if (TryParseModifyOtherKeysSequence(data, out var modifyOtherKeys))
        {
            return FormatParsedKey(modifyOtherKeys.Codepoint, modifyOtherKeys.Modifier);
        }

        if (_kittyProtocolActive && (data == "\u001b\r" || data == "\n"))
        {
            return "shift+enter";
        }

        if (LegacySequenceKeyIds.TryGetValue(data, out var legacyKeyId))
        {
            return legacyKeyId;
        }

        if (data == "\u001b") return "escape";
        if (data == "\u001c") return "ctrl+\\";
        if (data == "\u001d") return "ctrl+]";
        if (data == "\u001f") return "ctrl+-";
        if (data == "\u001b\u001b") return "ctrl+alt+[";
        if (data == "\u001b\u001c") return "ctrl+alt+\\";
        if (data == "\u001b\u001d") return "ctrl+alt+]";
        if (data == "\u001b\u001f") return "ctrl+alt+-";
        if (data == "\t") return "tab";
        if (data == "\r" || (!_kittyProtocolActive && data == "\n") || data == "\u001bOM") return "enter";
        if (data == "\0") return "ctrl+space";
        if (data == " ") return "space";
        if (data == "\u007f") return "backspace";
        if (data == "\b") return IsWindowsTerminalSession() ? "ctrl+backspace" : "backspace";
        if (data == "\u001b[Z") return "shift+tab";
        if (!_kittyProtocolActive && data == "\u001b\r") return "alt+enter";
        if (!_kittyProtocolActive && data == "\u001b ") return "alt+space";
        if (data == "\u001b\u007f" || data == "\u001b\b") return "alt+backspace";
        if (!_kittyProtocolActive && data == "\u001bB") return "alt+left";
        if (!_kittyProtocolActive && data == "\u001bF") return "alt+right";

        if (!_kittyProtocolActive && data.Length == 2 && data[0] == '\u001b')
        {
            var code = data[1];
            if (code is >= (char)1 and <= (char)26)
            {
                return $"ctrl+alt+{(char)(code + 96)}";
            }

            if ((code is >= 'a' and <= 'z') || (code is >= '0' and <= '9'))
            {
                return $"alt+{code}";
            }
        }

        if (data == "\u001b[A") return "up";
        if (data == "\u001b[B") return "down";
        if (data == "\u001b[C") return "right";
        if (data == "\u001b[D") return "left";
        if (data == "\u001b[H" || data == "\u001bOH") return "home";
        if (data == "\u001b[F" || data == "\u001bOF") return "end";
        if (data == "\u001b[3~") return "delete";
        if (data == "\u001b[5~") return "pageUp";
        if (data == "\u001b[6~") return "pageDown";

        if (data.Length == 1)
        {
            var code = data[0];
            if (code is >= (char)1 and <= (char)26)
            {
                return $"ctrl+{(char)(code + 96)}";
            }

            if (code is >= (char)32 and <= (char)126)
            {
                return data;
            }
        }

        return null;
    }

    public static string? DecodeKittyPrintable(string data)
    {
        var match = KittyCsiURegex.Match(data);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var codepoint))
        {
            return null;
        }

        var shiftedKey = TryParseOptionalInt(match.Groups[2].Value, out var shifted)
            ? shifted
            : (int?)null;
        var modValue = int.TryParse(match.Groups[4].Value, out var parsedModValue) ? parsedModValue : 1;
        var modifier = modValue - 1;

        if ((modifier & ~(ShiftModifier | LockMask)) != 0 ||
            (modifier & (AltModifier | CtrlModifier)) != 0)
        {
            return null;
        }

        var effectiveCodepoint = codepoint;
        if ((modifier & ShiftModifier) != 0 && shiftedKey.HasValue)
        {
            effectiveCodepoint = shiftedKey.Value;
        }

        effectiveCodepoint = NormalizeKittyFunctionalCodepoint(effectiveCodepoint);
        return CodepointToString(effectiveCodepoint);
    }

    public static string? DecodePrintableKey(string data) =>
        DecodeKittyPrintable(data) ?? DecodeModifyOtherKeysPrintable(data);

    private static bool MatchesEnter(string data, int modifier)
    {
        if (modifier == ShiftModifier)
        {
            if (MatchesKittySequence(data, EnterCodepoint, ShiftModifier) ||
                MatchesKittySequence(data, KeypadEnterCodepoint, ShiftModifier) ||
                MatchesModifyOtherKeys(data, EnterCodepoint, ShiftModifier))
            {
                return true;
            }

            return _kittyProtocolActive && (data == "\u001b\r" || data == "\n");
        }

        if (modifier == AltModifier)
        {
            if (MatchesKittySequence(data, EnterCodepoint, AltModifier) ||
                MatchesKittySequence(data, KeypadEnterCodepoint, AltModifier) ||
                MatchesModifyOtherKeys(data, EnterCodepoint, AltModifier))
            {
                return true;
            }

            return !_kittyProtocolActive && data == "\u001b\r";
        }

        if (modifier == 0)
        {
            return data == "\r" ||
                (!_kittyProtocolActive && data == "\n") ||
                data == "\u001bOM" ||
                MatchesKittySequence(data, EnterCodepoint, 0) ||
                MatchesKittySequence(data, KeypadEnterCodepoint, 0);
        }

        return MatchesKittySequence(data, EnterCodepoint, modifier) ||
            MatchesKittySequence(data, KeypadEnterCodepoint, modifier) ||
            MatchesModifyOtherKeys(data, EnterCodepoint, modifier);
    }

    private static bool MatchesBackspace(string data, int modifier)
    {
        if (modifier == AltModifier)
        {
            return data == "\u001b\u007f" ||
                data == "\u001b\b" ||
                MatchesKittySequence(data, BackspaceCodepoint, AltModifier) ||
                MatchesModifyOtherKeys(data, BackspaceCodepoint, AltModifier);
        }

        if (modifier == CtrlModifier)
        {
            return MatchesRawBackspace(data, CtrlModifier) ||
                MatchesKittySequence(data, BackspaceCodepoint, CtrlModifier) ||
                MatchesModifyOtherKeys(data, BackspaceCodepoint, CtrlModifier);
        }

        return modifier == 0
            ? MatchesRawBackspace(data, 0) ||
                MatchesKittySequence(data, BackspaceCodepoint, 0) ||
                MatchesModifyOtherKeys(data, BackspaceCodepoint, 0)
            : MatchesKittySequence(data, BackspaceCodepoint, modifier) ||
                MatchesModifyOtherKeys(data, BackspaceCodepoint, modifier);
    }

    private static bool MatchesFunctionalKey(string data, string legacyKey, int codepoint, int modifier)
    {
        if (modifier == 0)
        {
            return MatchesLegacySequence(data, legacyKey) || MatchesKittySequence(data, codepoint, 0);
        }

        return MatchesLegacyModifierSequence(data, legacyKey, modifier) ||
            MatchesKittySequence(data, codepoint, modifier);
    }

    private static bool MatchesDirectionalKey(string data, string legacyKey, int codepoint, int modifier)
    {
        if (modifier == 0)
        {
            return MatchesLegacySequence(data, legacyKey) || MatchesKittySequence(data, codepoint, 0);
        }

        return MatchesLegacyModifierSequence(data, legacyKey, modifier) ||
            MatchesKittySequence(data, codepoint, modifier);
    }

    private static bool MatchesPrintableKey(string data, string key, int modifier)
    {
        if (key.Length != 1)
        {
            return false;
        }

        var character = key[0];
        var isLetter = character is >= 'a' and <= 'z';
        var isDigit = character is >= '0' and <= '9';
        if (!isLetter && !isDigit && !SymbolKeys.Contains(character))
        {
            return false;
        }

        var codepoint = character;
        var rawCtrl = RawCtrlChar(key);
        if (modifier == CtrlModifier + AltModifier && !_kittyProtocolActive && rawCtrl is { } ctrlAlt && data == $"\u001b{ctrlAlt}")
        {
            return true;
        }

        if (modifier == AltModifier && !_kittyProtocolActive && (isLetter || isDigit) && data == $"\u001b{key}")
        {
            return true;
        }

        if (modifier == CtrlModifier)
        {
            return (rawCtrl is { } ctrl && data == ctrl) ||
                MatchesKittySequence(data, codepoint, CtrlModifier) ||
                MatchesPrintableModifyOtherKeys(data, codepoint, CtrlModifier);
        }

        if (modifier == ShiftModifier + CtrlModifier)
        {
            return MatchesKittySequence(data, codepoint, ShiftModifier + CtrlModifier) ||
                MatchesPrintableModifyOtherKeys(data, codepoint, ShiftModifier + CtrlModifier);
        }

        if (modifier == ShiftModifier)
        {
            return (isLetter && data == key.ToUpperInvariant()) ||
                MatchesKittySequence(data, codepoint, ShiftModifier) ||
                MatchesPrintableModifyOtherKeys(data, codepoint, ShiftModifier);
        }

        return modifier == 0
            ? data == key || MatchesKittySequence(data, codepoint, 0)
            : MatchesKittySequence(data, codepoint, modifier) ||
                MatchesPrintableModifyOtherKeys(data, codepoint, modifier);
    }

    private static bool TryParseKeyId(string keyId, out string key, out int modifier)
    {
        key = string.Empty;
        modifier = 0;
        if (string.IsNullOrWhiteSpace(keyId))
        {
            return false;
        }

        var parts = keyId.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        key = parts[^1];
        foreach (var part in parts[..^1])
        {
            modifier |= part switch
            {
                "shift" => ShiftModifier,
                "alt" => AltModifier,
                "ctrl" => CtrlModifier,
                "super" => SuperModifier,
                _ => 0
            };
        }

        return key.Length > 0;
    }

    private static bool TryParseKittySequence(string data, out ParsedKittySequence result)
    {
        var csiUMatch = KittyCsiURegex.Match(data);
        if (csiUMatch.Success && int.TryParse(csiUMatch.Groups[1].Value, out var csiCodepoint))
        {
            var shiftedKey = TryParseOptionalInt(csiUMatch.Groups[2].Value, out var shifted)
                ? shifted
                : (int?)null;
            var baseLayoutKey = TryParseOptionalInt(csiUMatch.Groups[3].Value, out var layout)
                ? layout
                : (int?)null;
            var modValue = int.TryParse(csiUMatch.Groups[4].Value, out var parsedModValue)
                ? parsedModValue
                : 1;
            var eventType = ParseEventType(csiUMatch.Groups[5].Value);
            _lastEventType = eventType;
            result = new ParsedKittySequence(csiCodepoint, shiftedKey, baseLayoutKey, modValue - 1, eventType);
            return true;
        }

        var arrowMatch = KittyArrowRegex.Match(data);
        if (arrowMatch.Success && int.TryParse(arrowMatch.Groups[1].Value, out var arrowModValue))
        {
            var eventType = ParseEventType(arrowMatch.Groups[2].Value);
            var codepoint = arrowMatch.Groups[3].Value[0] switch
            {
                'A' => ArrowUpCodepoint,
                'B' => ArrowDownCodepoint,
                'C' => ArrowRightCodepoint,
                'D' => ArrowLeftCodepoint,
                _ => 0
            };
            _lastEventType = eventType;
            result = new ParsedKittySequence(codepoint, null, null, arrowModValue - 1, eventType);
            return true;
        }

        var functionalMatch = KittyFunctionalRegex.Match(data);
        if (functionalMatch.Success &&
            int.TryParse(functionalMatch.Groups[1].Value, out var keyNum) &&
            TryMapFunctionalKeyNumber(keyNum, out var functionalCodepoint))
        {
            var modValue = int.TryParse(functionalMatch.Groups[2].Value, out var parsedModValue)
                ? parsedModValue
                : 1;
            var eventType = ParseEventType(functionalMatch.Groups[3].Value);
            _lastEventType = eventType;
            result = new ParsedKittySequence(functionalCodepoint, null, null, modValue - 1, eventType);
            return true;
        }

        var homeEndMatch = KittyHomeEndRegex.Match(data);
        if (homeEndMatch.Success && int.TryParse(homeEndMatch.Groups[1].Value, out var homeEndModValue))
        {
            var eventType = ParseEventType(homeEndMatch.Groups[2].Value);
            var codepoint = homeEndMatch.Groups[3].Value == "H" ? HomeCodepoint : EndCodepoint;
            _lastEventType = eventType;
            result = new ParsedKittySequence(codepoint, null, null, homeEndModValue - 1, eventType);
            return true;
        }

        result = default;
        return false;
    }

    private static bool MatchesKittySequence(string data, int expectedCodepoint, int expectedModifier)
    {
        if (!TryParseKittySequence(data, out var parsed))
        {
            return false;
        }

        var actualModifier = parsed.Modifier & ~LockMask;
        var expected = expectedModifier & ~LockMask;
        if (actualModifier != expected)
        {
            return false;
        }

        var normalizedCodepoint = NormalizeShiftedLetterIdentityCodepoint(
            NormalizeKittyFunctionalCodepoint(parsed.Codepoint),
            parsed.Modifier);
        var normalizedExpectedCodepoint = NormalizeShiftedLetterIdentityCodepoint(
            NormalizeKittyFunctionalCodepoint(expectedCodepoint),
            expectedModifier);

        if (normalizedCodepoint == normalizedExpectedCodepoint)
        {
            return true;
        }

        if (parsed.BaseLayoutKey == expectedCodepoint)
        {
            return !IsLatinLetter(normalizedCodepoint) && !IsKnownSymbol(normalizedCodepoint);
        }

        return false;
    }

    private static bool TryParseModifyOtherKeysSequence(string data, out ParsedModifyOtherKeysSequence result)
    {
        var match = ModifyOtherKeysRegex.Match(data);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var modValue) &&
            int.TryParse(match.Groups[2].Value, out var codepoint))
        {
            result = new ParsedModifyOtherKeysSequence(codepoint, modValue - 1);
            return true;
        }

        result = default;
        return false;
    }

    private static bool MatchesModifyOtherKeys(string data, int expectedKeycode, int expectedModifier) =>
        TryParseModifyOtherKeysSequence(data, out var parsed) &&
        parsed.Codepoint == expectedKeycode &&
        parsed.Modifier == expectedModifier;

    private static bool MatchesPrintableModifyOtherKeys(string data, int expectedKeycode, int expectedModifier)
    {
        if (expectedModifier == 0 || !TryParseModifyOtherKeysSequence(data, out var parsed) || parsed.Modifier != expectedModifier)
        {
            return false;
        }

        return NormalizeShiftedLetterIdentityCodepoint(parsed.Codepoint, parsed.Modifier) ==
            NormalizeShiftedLetterIdentityCodepoint(expectedKeycode, expectedModifier);
    }

    private static string? DecodeModifyOtherKeysPrintable(string data)
    {
        if (!TryParseModifyOtherKeysSequence(data, out var parsed))
        {
            return null;
        }

        var modifier = parsed.Modifier & ~LockMask;
        return (modifier & ~ShiftModifier) != 0 ? null : CodepointToString(parsed.Codepoint);
    }

    private static bool MatchesLegacySequence(string data, string key) =>
        LegacyKeySequences.TryGetValue(key, out var sequences) && sequences.Contains(data, StringComparer.Ordinal);

    private static bool MatchesLegacyModifierSequence(string data, string key, int modifier)
    {
        if (modifier == ShiftModifier)
        {
            return LegacyShiftSequences.TryGetValue(key, out var sequences) &&
                sequences.Contains(data, StringComparer.Ordinal);
        }

        return modifier == CtrlModifier &&
            LegacyCtrlSequences.TryGetValue(key, out var ctrlSequences) &&
            ctrlSequences.Contains(data, StringComparer.Ordinal);
    }

    private static string? FormatParsedKey(int codepoint, int modifier, int? baseLayoutKey = null)
    {
        var normalizedCodepoint = NormalizeKittyFunctionalCodepoint(codepoint);
        var identityCodepoint = NormalizeShiftedLetterIdentityCodepoint(normalizedCodepoint, modifier);
        var effectiveCodepoint = IsLatinLetter(identityCodepoint) || IsDigit(identityCodepoint) || IsKnownSymbol(identityCodepoint)
            ? identityCodepoint
            : baseLayoutKey ?? identityCodepoint;

        var keyName = effectiveCodepoint switch
        {
            EscapeCodepoint => "escape",
            TabCodepoint => "tab",
            EnterCodepoint or KeypadEnterCodepoint => "enter",
            SpaceCodepoint => "space",
            BackspaceCodepoint => "backspace",
            DeleteCodepoint => "delete",
            InsertCodepoint => "insert",
            HomeCodepoint => "home",
            EndCodepoint => "end",
            PageUpCodepoint => "pageUp",
            PageDownCodepoint => "pageDown",
            ArrowUpCodepoint => "up",
            ArrowDownCodepoint => "down",
            ArrowLeftCodepoint => "left",
            ArrowRightCodepoint => "right",
            _ when IsDigit(effectiveCodepoint) => ((char)effectiveCodepoint).ToString(),
            _ when IsLatinLetter(effectiveCodepoint) => ((char)effectiveCodepoint).ToString(),
            _ when IsKnownSymbol(effectiveCodepoint) => ((char)effectiveCodepoint).ToString(),
            _ => null
        };

        return keyName is null ? null : FormatKeyNameWithModifiers(keyName, modifier);
    }

    private static string? FormatKeyNameWithModifiers(string keyName, int modifier)
    {
        var effectiveModifier = modifier & ~LockMask;
        const int supportedModifierMask = ShiftModifier | CtrlModifier | AltModifier | SuperModifier;
        if ((effectiveModifier & ~supportedModifierMask) != 0)
        {
            return null;
        }

        var modifiers = new List<string>(4);
        if ((effectiveModifier & ShiftModifier) != 0) modifiers.Add("shift");
        if ((effectiveModifier & CtrlModifier) != 0) modifiers.Add("ctrl");
        if ((effectiveModifier & AltModifier) != 0) modifiers.Add("alt");
        if ((effectiveModifier & SuperModifier) != 0) modifiers.Add("super");

        return modifiers.Count == 0 ? keyName : $"{string.Join('+', modifiers)}+{keyName}";
    }

    private static TuiKeyEventType ParseEventType(string eventType)
    {
        if (!int.TryParse(eventType, out var parsed))
        {
            return TuiKeyEventType.Press;
        }

        return parsed switch
        {
            2 => TuiKeyEventType.Repeat,
            3 => TuiKeyEventType.Release,
            _ => TuiKeyEventType.Press
        };
    }

    private static int NormalizeKittyFunctionalCodepoint(int codepoint) =>
        KittyFunctionalKeyEquivalents.TryGetValue(codepoint, out var equivalent) ? equivalent : codepoint;

    private static int NormalizeShiftedLetterIdentityCodepoint(int codepoint, int modifier)
    {
        var effectiveModifier = modifier & ~LockMask;
        return (effectiveModifier & ShiftModifier) != 0 && codepoint is >= 'A' and <= 'Z'
            ? codepoint + 32
            : codepoint;
    }

    private static bool TryMapFunctionalKeyNumber(int keyNumber, out int codepoint)
    {
        codepoint = keyNumber switch
        {
            2 => InsertCodepoint,
            3 => DeleteCodepoint,
            5 => PageUpCodepoint,
            6 => PageDownCodepoint,
            7 => HomeCodepoint,
            8 => EndCodepoint,
            _ => 0
        };

        return codepoint != 0;
    }

    private static string? RawCtrlChar(string key)
    {
        var character = char.ToLowerInvariant(key[0]);
        if ((character is >= 'a' and <= 'z') || character is '[' or '\\' or ']' or '_')
        {
            return ((char)(character & 0x1f)).ToString();
        }

        return character == '-' ? ((char)31).ToString() : null;
    }

    private static bool MatchesRawBackspace(string data, int expectedModifier)
    {
        if (data == "\u007f")
        {
            return expectedModifier == 0;
        }

        return data == "\b" && (IsWindowsTerminalSession() ? expectedModifier == CtrlModifier : expectedModifier == 0);
    }

    private static bool IsWindowsTerminalSession() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")) &&
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION")) &&
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CLIENT")) &&
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY"));

    private static bool TryParseOptionalInt(string value, out int parsed)
    {
        if (string.IsNullOrEmpty(value))
        {
            parsed = 0;
            return false;
        }

        return int.TryParse(value, out parsed);
    }

    private static string? CodepointToString(int codepoint)
    {
        if (codepoint < 32)
        {
            return null;
        }

        try
        {
            return char.ConvertFromUtf32(codepoint);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool IsDigit(int codepoint) => codepoint is >= '0' and <= '9';

    private static bool IsLatinLetter(int codepoint) => codepoint is >= 'a' and <= 'z';

    private static bool IsKnownSymbol(int codepoint) =>
        codepoint <= char.MaxValue && SymbolKeys.Contains((char)codepoint);

    private readonly record struct ParsedKittySequence(
        int Codepoint,
        int? ShiftedKey,
        int? BaseLayoutKey,
        int Modifier,
        TuiKeyEventType EventType);

    private readonly record struct ParsedModifyOtherKeysSequence(int Codepoint, int Modifier);
}
