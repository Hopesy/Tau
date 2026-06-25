using System.Globalization;
using System.Text;

namespace Tau.Tui.Rendering;

public static class TuiText
{
    public static int VisibleWidth(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        if (IsPrintableAscii(text))
        {
            return text.Length;
        }

        var width = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (TryReadEscape(text, index, out var escapeLength))
            {
                index += escapeLength;
                continue;
            }

            var rune = Rune.GetRuneAt(text, index);
            if (rune.Value == '\t')
            {
                width += 3;
            }
            else if (IsZeroWidth(rune))
            {
                width += 0;
            }
            else
            {
                width += IsWide(rune) ? 2 : 1;
            }

            index += rune.Utf16SequenceLength;
        }

        return width;
    }

    public static string TruncateToWidth(string? text, int maxWidth, string ellipsis = "...", bool pad = false)
    {
        if (maxWidth <= 0)
        {
            return string.Empty;
        }

        text ??= string.Empty;
        var textWidth = VisibleWidth(text);
        if (textWidth <= maxWidth)
        {
            return pad ? PadRightToWidth(text, maxWidth) : text;
        }

        var ellipsisWidth = VisibleWidth(ellipsis);
        if (ellipsisWidth >= maxWidth)
        {
            var clippedEllipsis = TakePrefixByWidth(ellipsis, maxWidth, out var clippedWidth);
            return pad ? clippedEllipsis + new string(' ', Math.Max(0, maxWidth - clippedWidth)) : clippedEllipsis;
        }

        var prefixWidthLimit = maxWidth - ellipsisWidth;
        var prefix = TakePrefixByWidth(text, prefixWidthLimit, out var prefixWidth);
        var result = prefix + ellipsis;
        return pad ? result + new string(' ', Math.Max(0, maxWidth - prefixWidth - ellipsisWidth)) : result;
    }

    public static string PadRightToWidth(string? text, int width)
    {
        text ??= string.Empty;
        var visibleWidth = VisibleWidth(text);
        if (visibleWidth >= width)
        {
            return text;
        }

        return text + new string(' ', width - visibleWidth);
    }

    public static string ApplyBackgroundToLine(string? line, int width, Func<string, string> backgroundFormatter)
    {
        ArgumentNullException.ThrowIfNull(backgroundFormatter);
        return backgroundFormatter(PadRightToWidth(line, Math.Max(1, width)));
    }

    public static string NormalizeSingleLine(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    public static IReadOnlyList<string> Wrap(string? text, int width)
    {
        return WrapTextWithAnsi(text, width);
    }

    public static IReadOnlyList<string> WrapTextWithAnsi(string? text, int width)
    {
        width = Math.Max(1, width);
        text ??= string.Empty;
        if (text.Length == 0)
        {
            return [string.Empty];
        }

        var output = new List<string>();
        var tracker = new AnsiStyleTracker();
        foreach (var paragraph in text.Replace("\t", "   ", StringComparison.Ordinal).Split('\n'))
        {
            var line = paragraph.TrimEnd('\r');
            var prefix = output.Count == 0 ? string.Empty : tracker.GetActiveCodes();
            WrapLine(prefix + line, width, output);
            UpdateTrackerFromText(line, tracker);
        }

        return output.Count == 0 ? [string.Empty] : output;
    }

    public static string SliceByColumn(string? line, int startColumn, int length, bool strict = false) =>
        SliceWithWidth(line, startColumn, length, strict).Text;

    public static (string Text, int Width) SliceWithWidth(
        string? line,
        int startColumn,
        int length,
        bool strict = false)
    {
        line ??= string.Empty;
        startColumn = Math.Max(0, startColumn);
        if (length <= 0 || line.Length == 0)
        {
            return (string.Empty, 0);
        }

        var endColumn = startColumn + length;
        var result = new StringBuilder();
        var pendingAnsi = new StringBuilder();
        var currentColumn = 0;
        var resultWidth = 0;
        var index = 0;

        while (index < line.Length)
        {
            if (TryReadEscape(line, index, out var escapeLength))
            {
                var escape = line.AsSpan(index, escapeLength);
                if (currentColumn >= startColumn && currentColumn < endColumn)
                {
                    result.Append(escape);
                }
                else if (currentColumn < startColumn)
                {
                    pendingAnsi.Append(escape);
                }

                index += escapeLength;
                continue;
            }

            var nextEscape = FindNextEscape(line, index);
            foreach (var textElement in EnumerateTextElements(line[index..nextEscape]))
            {
                var elementWidth = TextElementWidth(textElement);
                var inRange = currentColumn >= startColumn && currentColumn < endColumn;
                var fits = !strict || currentColumn + elementWidth <= endColumn;
                if (inRange && fits)
                {
                    if (pendingAnsi.Length > 0)
                    {
                        result.Append(pendingAnsi);
                        pendingAnsi.Clear();
                    }

                    result.Append(textElement);
                    resultWidth += elementWidth;
                }

                currentColumn += elementWidth;
                if (currentColumn >= endColumn)
                {
                    return (result.ToString(), resultWidth);
                }
            }

            index = nextEscape;
        }

        return (result.ToString(), resultWidth);
    }

    private static void WrapLine(string line, int width, List<string> output)
    {
        if (line.Length == 0)
        {
            output.Add(string.Empty);
            return;
        }

        var current = new StringBuilder();
        var currentWidth = 0;
        var tracker = new AnsiStyleTracker();
        foreach (var token in SplitWrappingTokensWithAnsi(line))
        {
            var tokenWidth = VisibleWidth(token);
            var tokenIsWhitespace = IsWhitespaceToken(token);
            if (!tokenIsWhitespace && tokenWidth > width)
            {
                FlushCurrent();
                BreakLongToken(token, width, tracker, output, out var remainder, out var remainderWidth);
                current.Append(remainder);
                currentWidth = remainderWidth;
                continue;
            }

            if (currentWidth > 0 && currentWidth + tokenWidth > width)
            {
                var wrapped = current.ToString().TrimEnd();
                wrapped += tracker.GetLineEndReset();
                output.Add(wrapped);
                current.Clear();
                currentWidth = 0;
                if (tokenIsWhitespace)
                {
                    current.Append(tracker.GetActiveCodes());
                    continue;
                }

                current.Append(tracker.GetActiveCodes());
            }

            current.Append(token);
            currentWidth += tokenWidth;
            UpdateTrackerFromText(token, tracker);
        }

        FlushCurrent();

        void FlushCurrent()
        {
            if (current.Length == 0)
            {
                return;
            }

            output.Add(current.ToString().TrimEnd());
            current.Clear();
            currentWidth = 0;
        }
    }

    private static IEnumerable<string> SplitWrappingTokensWithAnsi(string line)
    {
        var current = new StringBuilder();
        var pendingAnsi = new StringBuilder();
        bool? whitespace = null;
        var index = 0;
        while (index < line.Length)
        {
            if (TryReadEscape(line, index, out var escapeLength))
            {
                pendingAnsi.Append(line.AsSpan(index, escapeLength));
                index += escapeLength;
                continue;
            }

            var nextEscape = FindNextEscape(line, index);
            foreach (var textElement in EnumerateTextElements(line[index..nextEscape]))
            {
                var isWhitespace = string.IsNullOrWhiteSpace(textElement);
                if (whitespace is not null && whitespace.Value != isWhitespace && current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                if (pendingAnsi.Length > 0)
                {
                    current.Append(pendingAnsi);
                    pendingAnsi.Clear();
                }

                whitespace = isWhitespace;
                current.Append(textElement);
            }

            index = nextEscape;
        }

        if (pendingAnsi.Length > 0)
        {
            if (current.Length > 0)
            {
                current.Append(pendingAnsi);
            }
            else
            {
                current.Append(pendingAnsi);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static void BreakLongToken(
        string token,
        int width,
        AnsiStyleTracker tracker,
        List<string> output,
        out string remainder,
        out int remainderWidth)
    {
        var current = new StringBuilder(tracker.GetActiveCodes());
        var currentWidth = 0;
        var index = 0;
        while (index < token.Length)
        {
            if (TryReadEscape(token, index, out var escapeLength))
            {
                var escape = token.Substring(index, escapeLength);
                current.Append(escape);
                tracker.Process(escape);
                index += escapeLength;
                continue;
            }

            var nextEscape = FindNextEscape(token, index);
            foreach (var textElement in EnumerateTextElements(token[index..nextEscape]))
            {
                var elementWidth = TextElementWidth(textElement);
                if (currentWidth > 0 && currentWidth + elementWidth > width)
                {
                    current.Append(tracker.GetLineEndReset());
                    output.Add(current.ToString());
                    current.Clear();
                    current.Append(tracker.GetActiveCodes());
                    currentWidth = 0;
                }

                if (elementWidth <= width)
                {
                    current.Append(textElement);
                    currentWidth += elementWidth;
                }
            }

            index = nextEscape;
        }

        remainder = current.ToString();
        remainderWidth = currentWidth;
    }

    private static string TakePrefixByWidth(string text, int maxWidth, out int width)
    {
        var builder = new StringBuilder();
        width = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (TryReadEscape(text, index, out var escapeLength))
            {
                builder.Append(text.AsSpan(index, escapeLength));
                index += escapeLength;
                continue;
            }

            var nextEscape = FindNextEscape(text, index);
            var consumedText = false;
            foreach (var textElement in EnumerateTextElements(text[index..nextEscape]))
            {
                var runeWidth = TextElementWidth(textElement);
                if (width + runeWidth > maxWidth)
                {
                    return builder.ToString();
                }

                builder.Append(textElement);
                width += runeWidth;
                index += textElement.Length;
                consumedText = true;
            }

            if (consumedText)
            {
                continue;
            }

            var rune = Rune.GetRuneAt(text, index);
            var fallbackRuneWidth = rune.Value == '\t' ? 3 : IsZeroWidth(rune) ? 0 : IsWide(rune) ? 2 : 1;
            if (width + fallbackRuneWidth > maxWidth)
            {
                break;
            }

            builder.Append(text.AsSpan(index, rune.Utf16SequenceLength));
            width += fallbackRuneWidth;
            index += rune.Utf16SequenceLength;
        }

        return builder.ToString();
    }

    private static void UpdateTrackerFromText(string text, AnsiStyleTracker tracker)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (TryReadEscape(text, index, out var escapeLength))
            {
                tracker.Process(text.Substring(index, escapeLength));
                index += escapeLength;
                continue;
            }

            index++;
        }
    }

    private static int FindNextEscape(string text, int index)
    {
        while (index < text.Length)
        {
            if (TryReadEscape(text, index, out _))
            {
                return index;
            }

            index++;
        }

        return text.Length;
    }

    private static int TextElementWidth(string textElement)
    {
        if (textElement.Length == 0)
        {
            return 0;
        }

        if (textElement == "\t")
        {
            return 3;
        }

        var width = 0;
        var index = 0;
        while (index < textElement.Length)
        {
            var rune = Rune.GetRuneAt(textElement, index);
            width += IsZeroWidth(rune) ? 0 : IsWide(rune) ? 2 : 1;
            index += rune.Utf16SequenceLength;
        }

        return width;
    }

    private static bool IsWhitespaceToken(string token)
    {
        var hasText = false;
        var index = 0;
        while (index < token.Length)
        {
            if (TryReadEscape(token, index, out var escapeLength))
            {
                index += escapeLength;
                continue;
            }

            var rune = Rune.GetRuneAt(token, index);
            hasText = true;
            if (!Rune.IsWhiteSpace(rune))
            {
                return false;
            }

            index += rune.Utf16SequenceLength;
        }

        return hasText;
    }

    private static IEnumerable<string> EnumerateTextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return enumerator.GetTextElement();
        }
    }

    private static bool IsPrintableAscii(string text)
    {
        foreach (var ch in text)
        {
            if (ch is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadEscape(string text, int index, out int length)
    {
        length = 0;
        if (index >= text.Length || text[index] != '\u001b' || index + 1 >= text.Length)
        {
            return false;
        }

        var next = text[index + 1];
        if (next == '[')
        {
            var end = index + 2;
            while (end < text.Length && !char.IsLetter(text[end]))
            {
                end++;
            }

            if (end < text.Length)
            {
                length = end - index + 1;
                return true;
            }
        }
        else if (next is ']' or '_')
        {
            var end = index + 2;
            while (end < text.Length)
            {
                if (text[end] == '\u0007')
                {
                    length = end - index + 1;
                    return true;
                }

                if (text[end] == '\u001b' && end + 1 < text.Length && text[end + 1] == '\\')
                {
                    length = end - index + 2;
                    return true;
                }

                end++;
            }
        }

        return false;
    }

    private sealed class AnsiStyleTracker
    {
        private bool _bold;
        private bool _dim;
        private bool _italic;
        private bool _underline;
        private bool _blink;
        private bool _inverse;
        private bool _hidden;
        private bool _strikethrough;
        private string? _foregroundColor;
        private string? _backgroundColor;
        private ActiveHyperlink? _activeHyperlink;

        public void Process(string escape)
        {
            if (TryParseOsc8Hyperlink(escape, out var hyperlink))
            {
                _activeHyperlink = hyperlink;
                return;
            }

            if (!escape.EndsWith('m') || !escape.StartsWith("\u001b[", StringComparison.Ordinal))
            {
                return;
            }

            var body = escape[2..^1];
            if (body.Length == 0 || string.Equals(body, "0", StringComparison.Ordinal))
            {
                ResetSgr();
                return;
            }

            var parts = body.Split(';');
            var index = 0;
            while (index < parts.Length)
            {
                if (!int.TryParse(parts[index], out var code))
                {
                    index++;
                    continue;
                }

                if ((code == 38 || code == 48) &&
                    index + 2 < parts.Length &&
                    string.Equals(parts[index + 1], "5", StringComparison.Ordinal))
                {
                    var color = $"{parts[index]};{parts[index + 1]};{parts[index + 2]}";
                    if (code == 38)
                    {
                        _foregroundColor = color;
                    }
                    else
                    {
                        _backgroundColor = color;
                    }

                    index += 3;
                    continue;
                }

                if ((code == 38 || code == 48) &&
                    index + 4 < parts.Length &&
                    string.Equals(parts[index + 1], "2", StringComparison.Ordinal))
                {
                    var color = $"{parts[index]};{parts[index + 1]};{parts[index + 2]};{parts[index + 3]};{parts[index + 4]}";
                    if (code == 38)
                    {
                        _foregroundColor = color;
                    }
                    else
                    {
                        _backgroundColor = color;
                    }

                    index += 5;
                    continue;
                }

                ProcessSgrCode(code);
                index++;
            }
        }

        public string GetActiveCodes()
        {
            var codes = new List<string>();
            if (_bold) codes.Add("1");
            if (_dim) codes.Add("2");
            if (_italic) codes.Add("3");
            if (_underline) codes.Add("4");
            if (_blink) codes.Add("5");
            if (_inverse) codes.Add("7");
            if (_hidden) codes.Add("8");
            if (_strikethrough) codes.Add("9");
            if (_foregroundColor is not null) codes.Add(_foregroundColor);
            if (_backgroundColor is not null) codes.Add(_backgroundColor);

            var builder = new StringBuilder();
            if (codes.Count > 0)
            {
                builder.Append("\u001b[");
                builder.Append(string.Join(';', codes));
                builder.Append('m');
            }

            if (_activeHyperlink is { } hyperlink)
            {
                builder.Append(FormatOsc8Hyperlink(hyperlink));
            }

            return builder.ToString();
        }

        public string GetLineEndReset()
        {
            var builder = new StringBuilder();
            if (_underline)
            {
                builder.Append("\u001b[24m");
            }

            if (_activeHyperlink is { } hyperlink)
            {
                builder.Append(FormatOsc8Close(hyperlink.Terminator));
            }

            return builder.ToString();
        }

        private void ProcessSgrCode(int code)
        {
            switch (code)
            {
                case 0:
                    ResetSgr();
                    break;
                case 1:
                    _bold = true;
                    break;
                case 2:
                    _dim = true;
                    break;
                case 3:
                    _italic = true;
                    break;
                case 4:
                    _underline = true;
                    break;
                case 5:
                    _blink = true;
                    break;
                case 7:
                    _inverse = true;
                    break;
                case 8:
                    _hidden = true;
                    break;
                case 9:
                    _strikethrough = true;
                    break;
                case 21:
                    _bold = false;
                    break;
                case 22:
                    _bold = false;
                    _dim = false;
                    break;
                case 23:
                    _italic = false;
                    break;
                case 24:
                    _underline = false;
                    break;
                case 25:
                    _blink = false;
                    break;
                case 27:
                    _inverse = false;
                    break;
                case 28:
                    _hidden = false;
                    break;
                case 29:
                    _strikethrough = false;
                    break;
                case 39:
                    _foregroundColor = null;
                    break;
                case 49:
                    _backgroundColor = null;
                    break;
                default:
                    if (code is >= 30 and <= 37 or >= 90 and <= 97)
                    {
                        _foregroundColor = code.ToString(CultureInfo.InvariantCulture);
                    }
                    else if (code is >= 40 and <= 47 or >= 100 and <= 107)
                    {
                        _backgroundColor = code.ToString(CultureInfo.InvariantCulture);
                    }

                    break;
            }
        }

        private void ResetSgr()
        {
            _bold = false;
            _dim = false;
            _italic = false;
            _underline = false;
            _blink = false;
            _inverse = false;
            _hidden = false;
            _strikethrough = false;
            _foregroundColor = null;
            _backgroundColor = null;
        }

        private static bool TryParseOsc8Hyperlink(string escape, out ActiveHyperlink? hyperlink)
        {
            hyperlink = null;
            if (!escape.StartsWith("\u001b]8;", StringComparison.Ordinal))
            {
                return false;
            }

            var terminator = escape.EndsWith('\u0007') ? "\u0007" : "\u001b\\";
            var bodyEnd = terminator == "\u0007" ? escape.Length - 1 : escape.Length - 2;
            var body = escape[4..bodyEnd];
            var separator = body.IndexOf(';', StringComparison.Ordinal);
            if (separator < 0)
            {
                return false;
            }

            var url = body[(separator + 1)..];
            if (url.Length == 0)
            {
                return true;
            }

            hyperlink = new ActiveHyperlink(body[..separator], url, terminator);
            return true;
        }

        private static string FormatOsc8Hyperlink(ActiveHyperlink hyperlink) =>
            $"\u001b]8;{hyperlink.Parameters};{hyperlink.Url}{hyperlink.Terminator}";

        private static string FormatOsc8Close(string terminator) => $"\u001b]8;;{terminator}";
    }

    private sealed record ActiveHyperlink(string Parameters, string Url, string Terminator);

    private static bool IsZeroWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Surrogate;
    }

    private static bool IsWide(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x1100 and <= 0x115F
            or >= 0x2329 and <= 0x232A
            or >= 0x2E80 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F000 and <= 0x1FAFF;
    }
}
