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
        width = Math.Max(1, width);
        text ??= string.Empty;
        if (text.Length == 0)
        {
            return [string.Empty];
        }

        var output = new List<string>();
        foreach (var paragraph in text.Replace("\t", "   ", StringComparison.Ordinal).Split('\n'))
        {
            WrapLine(paragraph.TrimEnd('\r'), width, output);
        }

        return output.Count == 0 ? [string.Empty] : output;
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
        foreach (var token in SplitWrappingTokens(line))
        {
            var tokenWidth = VisibleWidth(token);
            var tokenIsWhitespace = string.IsNullOrWhiteSpace(token);
            if (!tokenIsWhitespace && tokenWidth > width)
            {
                FlushCurrent();
                BreakLongToken(token, width, output, out var remainder, out var remainderWidth);
                current.Append(remainder);
                currentWidth = remainderWidth;
                continue;
            }

            if (currentWidth > 0 && currentWidth + tokenWidth > width)
            {
                output.Add(current.ToString().TrimEnd());
                current.Clear();
                currentWidth = 0;
                if (tokenIsWhitespace)
                {
                    continue;
                }
            }

            current.Append(token);
            currentWidth += tokenWidth;
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

    private static IEnumerable<string> SplitWrappingTokens(string line)
    {
        var current = new StringBuilder();
        bool? whitespace = null;
        foreach (var textElement in EnumerateTextElements(line))
        {
            var isWhitespace = string.IsNullOrWhiteSpace(textElement);
            if (whitespace is not null && whitespace.Value != isWhitespace && current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }

            whitespace = isWhitespace;
            current.Append(textElement);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static void BreakLongToken(
        string token,
        int width,
        List<string> output,
        out string remainder,
        out int remainderWidth)
    {
        var current = new StringBuilder();
        var currentWidth = 0;
        foreach (var textElement in EnumerateTextElements(token))
        {
            var elementWidth = VisibleWidth(textElement);
            if (currentWidth > 0 && currentWidth + elementWidth > width)
            {
                output.Add(current.ToString());
                current.Clear();
                currentWidth = 0;
            }

            if (elementWidth <= width)
            {
                current.Append(textElement);
                currentWidth += elementWidth;
            }
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

            var rune = Rune.GetRuneAt(text, index);
            var runeWidth = rune.Value == '\t' ? 3 : IsZeroWidth(rune) ? 0 : IsWide(rune) ? 2 : 1;
            if (width + runeWidth > maxWidth)
            {
                break;
            }

            builder.Append(text.AsSpan(index, rune.Utf16SequenceLength));
            width += runeWidth;
            index += rune.Utf16SequenceLength;
        }

        return builder.ToString();
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
