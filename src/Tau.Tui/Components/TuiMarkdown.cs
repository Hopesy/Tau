using System.Text;
using System.Text.RegularExpressions;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public sealed record TuiDefaultTextStyle(
    Func<string, string>? Color = null,
    Func<string, string>? BackgroundColor = null,
    bool Bold = false,
    bool Italic = false,
    bool Strikethrough = false,
    bool Underline = false);

public sealed class TuiMarkdownTheme
{
    public Func<string, string> Heading { get; init; } = static value => value;
    public Func<string, string> Link { get; init; } = static value => value;
    public Func<string, string> LinkUrl { get; init; } = static value => value;
    public Func<string, string> Code { get; init; } = static value => value;
    public Func<string, string> CodeBlock { get; init; } = static value => value;
    public Func<string, string> CodeBlockBorder { get; init; } = static value => value;
    public Func<string, string> Quote { get; init; } = static value => value;
    public Func<string, string> QuoteBorder { get; init; } = static value => value;
    public Func<string, string> HorizontalRule { get; init; } = static value => value;
    public Func<string, string> ListBullet { get; init; } = static value => value;
    public Func<string, string> Bold { get; init; } = static value => value;
    public Func<string, string> Italic { get; init; } = static value => value;
    public Func<string, string> Strikethrough { get; init; } = static value => value;
    public Func<string, string> Underline { get; init; } = static value => value;
    public Func<string, string?, IReadOnlyList<string>>? HighlightCode { get; init; } = TuiSyntaxHighlighter.HighlightLines;
    public string CodeBlockIndent { get; init; } = "  ";
}

public sealed class TuiMarkdown : ITuiComponent
{
    private static readonly Regex HeadingRegex = new("^(#{1,6})\\s+(.+?)\\s*$", RegexOptions.Compiled);
    private static readonly Regex OrderedListRegex = new("^(\\s*)(\\d+)\\.\\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex UnorderedListRegex = new("^(\\s*)[-+*]\\s+(.*)$", RegexOptions.Compiled);

    private readonly int _paddingX;
    private readonly int _paddingY;
    private readonly TuiMarkdownTheme _theme;
    private readonly TuiDefaultTextStyle? _defaultTextStyle;
    private readonly bool _enableHyperlinks;
    private string _text;
    private string? _cachedText;
    private int? _cachedWidth;
    private IReadOnlyList<string>? _cachedLines;

    public TuiMarkdown(
        string text,
        int paddingX = 0,
        int paddingY = 0,
        TuiMarkdownTheme? theme = null,
        TuiDefaultTextStyle? defaultTextStyle = null,
        bool enableHyperlinks = false)
    {
        _text = text;
        _paddingX = Math.Max(0, paddingX);
        _paddingY = Math.Max(0, paddingY);
        _theme = theme ?? new TuiMarkdownTheme();
        _defaultTextStyle = defaultTextStyle;
        _enableHyperlinks = enableHyperlinks;
    }

    public string Text => _text;

    public void SetText(string text)
    {
        if (string.Equals(_text, text, StringComparison.Ordinal))
        {
            return;
        }

        _text = text;
        Invalidate();
    }

    public void Invalidate()
    {
        _cachedText = null;
        _cachedWidth = null;
        _cachedLines = null;
    }

    public IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width);
        if (_cachedLines is not null && _cachedWidth == width && string.Equals(_cachedText, _text, StringComparison.Ordinal))
        {
            return _cachedLines;
        }

        if (string.IsNullOrWhiteSpace(_text))
        {
            _cachedText = _text;
            _cachedWidth = width;
            _cachedLines = [];
            return _cachedLines;
        }

        var contentWidth = Math.Max(1, width - (_paddingX * 2));
        var normalized = _text.Replace("\t", "   ", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal);
        var rendered = RenderBlocks(normalized.Split('\n'), contentWidth);
        var wrapped = new List<string>();
        foreach (var line in rendered)
        {
            if (TuiTerminalImage.IsImageLine(line))
            {
                wrapped.Add(line);
                continue;
            }

            wrapped.AddRange(TuiText.Wrap(line, contentWidth));
        }

        var result = new List<string>();
        var emptyLine = ApplyBackground(TuiText.PadRightToWidth(string.Empty, width), width);
        for (var i = 0; i < _paddingY; i++)
        {
            result.Add(emptyLine);
        }

        var left = new string(' ', _paddingX);
        var right = new string(' ', _paddingX);
        foreach (var line in wrapped)
        {
            if (TuiTerminalImage.IsImageLine(line))
            {
                result.Add(line);
                continue;
            }

            var padded = TuiText.PadRightToWidth(left + line + right, width);
            result.Add(ApplyBackground(padded, width));
        }

        for (var i = 0; i < _paddingY; i++)
        {
            result.Add(emptyLine);
        }

        _cachedText = _text;
        _cachedWidth = width;
        _cachedLines = result;
        return result;
    }

    private IReadOnlyList<string> RenderBlocks(IReadOnlyList<string> sourceLines, int width)
    {
        var lines = new List<string>();
        var index = 0;
        while (index < sourceLines.Count)
        {
            var line = sourceLines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                lines.Add(string.Empty);
                index++;
                continue;
            }

            if (TryRenderFencedCode(sourceLines, ref index, lines))
            {
                continue;
            }

            if (TryRenderTable(sourceLines, ref index, width, lines))
            {
                continue;
            }

            if (TryRenderHeading(line, lines))
            {
                index++;
                continue;
            }

            if (IsHorizontalRule(line))
            {
                lines.Add(_theme.HorizontalRule(new string('\u2500', Math.Min(width, 80))));
                index++;
                continue;
            }

            if (TryRenderBlockquote(sourceLines, ref index, width, lines))
            {
                continue;
            }

            if (TryRenderList(sourceLines, ref index, lines))
            {
                continue;
            }

            lines.Add(RenderInline(CollectParagraph(sourceLines, ref index)));
        }

        return lines;
    }

    private bool TryRenderFencedCode(IReadOnlyList<string> sourceLines, ref int index, List<string> output)
    {
        var start = sourceLines[index].TrimStart();
        if (!start.StartsWith("```", StringComparison.Ordinal) && !start.StartsWith("~~~", StringComparison.Ordinal))
        {
            return false;
        }

        var fence = start[..3];
        var lang = start.Length > 3 ? start[3..].Trim() : string.Empty;
        var code = new List<string>();
        index++;
        while (index < sourceLines.Count)
        {
            var candidate = sourceLines[index].TrimStart();
            if (candidate.StartsWith(fence, StringComparison.Ordinal))
            {
                index++;
                break;
            }

            code.Add(sourceLines[index]);
            index++;
        }

        output.Add(_theme.CodeBlockBorder($"```{lang}"));
        if (_theme.HighlightCode is { } highlighter)
        {
            foreach (var highlighted in highlighter(string.Join('\n', code), string.IsNullOrWhiteSpace(lang) ? null : lang))
            {
                output.Add(_theme.CodeBlockIndent + highlighted);
            }
        }
        else
        {
            foreach (var codeLine in code)
            {
                output.Add(_theme.CodeBlockIndent + _theme.CodeBlock(codeLine));
            }
        }

        output.Add(_theme.CodeBlockBorder("```"));
        return true;
    }

    private bool TryRenderHeading(string line, List<string> output)
    {
        var match = HeadingRegex.Match(line);
        if (!match.Success)
        {
            return false;
        }

        var depth = match.Groups[1].Value.Length;
        var heading = RenderInline(match.Groups[2].Value, applyDefaultStyle: false);
        Func<string, string> style = depth == 1
            ? value => _theme.Heading(_theme.Bold(_theme.Underline(value)))
            : value => _theme.Heading(_theme.Bold(value));

        output.Add(depth >= 3 ? style($"{new string('#', depth)} ") + style(heading) : style(heading));
        return true;
    }

    private bool TryRenderBlockquote(IReadOnlyList<string> sourceLines, ref int index, int width, List<string> output)
    {
        if (!sourceLines[index].TrimStart().StartsWith('>'))
        {
            return false;
        }

        var quoteLines = new List<string>();
        while (index < sourceLines.Count)
        {
            var line = sourceLines[index];
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith('>'))
            {
                break;
            }

            var content = trimmed.Length > 1 ? trimmed[1..] : string.Empty;
            quoteLines.Add(content.StartsWith(' ') ? content[1..] : content);
            index++;
        }

        var rendered = RenderBlocks(quoteLines, Math.Max(1, width - 2));
        foreach (var quoteLine in rendered)
        {
            foreach (var wrappedLine in TuiText.Wrap(_theme.Quote(quoteLine), Math.Max(1, width - 2)))
            {
                output.Add(_theme.QuoteBorder("\u2502 ") + wrappedLine);
            }
        }

        return true;
    }

    private bool TryRenderList(IReadOnlyList<string> sourceLines, ref int index, List<string> output)
    {
        if (!TryParseListMarker(sourceLines[index], out _))
        {
            return false;
        }

        while (index < sourceLines.Count && TryParseListMarker(sourceLines[index], out var marker))
        {
            var indent = new string(' ', marker.Depth * 2);
            output.Add(indent + _theme.ListBullet(marker.Bullet) + RenderInline(marker.Text));
            index++;

            while (index < sourceLines.Count &&
                !string.IsNullOrWhiteSpace(sourceLines[index]) &&
                !TryParseListMarker(sourceLines[index], out _) &&
                !IsBlockStart(sourceLines, index))
            {
                output.Add(indent + "  " + RenderInline(sourceLines[index].Trim()));
                index++;
            }
        }

        return true;
    }

    private bool TryRenderTable(IReadOnlyList<string> sourceLines, ref int index, int width, List<string> output)
    {
        if (index + 1 >= sourceLines.Count ||
            !TrySplitTableRow(sourceLines[index], out var headers) ||
            !TrySplitTableRow(sourceLines[index + 1], out var separators) ||
            headers.Count == 0 ||
            separators.Count < headers.Count ||
            !separators.Take(headers.Count).All(IsTableSeparatorCell))
        {
            return false;
        }

        var rows = new List<IReadOnlyList<string>>();
        var rawRows = new List<string> { sourceLines[index], sourceLines[index + 1] };
        index += 2;
        while (index < sourceLines.Count && TrySplitTableRow(sourceLines[index], out var row))
        {
            if (row.Count == 0)
            {
                break;
            }

            rows.Add(NormalizeCellCount(row, headers.Count));
            rawRows.Add(sourceLines[index]);
            index++;
        }

        output.AddRange(RenderTable(headers, rows, rawRows, width));
        return true;
    }

    private IReadOnlyList<string> RenderTable(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<string> rawRows,
        int width)
    {
        var columnCount = headers.Count;
        var borderOverhead = 3 * columnCount + 1;
        var availableForCells = width - borderOverhead;
        if (availableForCells < columnCount)
        {
            return rawRows.SelectMany(row => TuiText.Wrap(row, width)).ToArray();
        }

        var renderedHeaders = headers.Select(cell => RenderInline(cell)).ToArray();
        var renderedRows = rows
            .Select(row => NormalizeCellCount(row, columnCount).Select(cell => RenderInline(cell)).ToArray())
            .ToArray();
        var naturalWidths = new int[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            naturalWidths[i] = Math.Max(1, TuiText.VisibleWidth(renderedHeaders[i]));
        }

        foreach (var row in renderedRows)
        {
            for (var i = 0; i < columnCount; i++)
            {
                naturalWidths[i] = Math.Max(naturalWidths[i], TuiText.VisibleWidth(row[i]));
            }
        }

        var widths = FitColumnWidths(naturalWidths, availableForCells);
        var lines = new List<string>
        {
            BorderLine('\u250c', '\u252c', '\u2510', widths),
        };

        AppendTableRow(lines, renderedHeaders, widths, header: true);
        lines.Add(BorderLine('\u251c', '\u253c', '\u2524', widths));
        for (var rowIndex = 0; rowIndex < renderedRows.Length; rowIndex++)
        {
            AppendTableRow(lines, renderedRows[rowIndex], widths, header: false);
            if (rowIndex < renderedRows.Length - 1)
            {
                lines.Add(BorderLine('\u251c', '\u253c', '\u2524', widths));
            }
        }

        lines.Add(BorderLine('\u2514', '\u2534', '\u2518', widths));
        return lines;
    }

    private void AppendTableRow(List<string> lines, IReadOnlyList<string> cells, IReadOnlyList<int> widths, bool header)
    {
        var wrappedCells = cells
            .Select((cell, index) => TuiText.Wrap(cell, widths[index]))
            .ToArray();
        var lineCount = wrappedCells.Max(static cell => cell.Count);
        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            var row = new StringBuilder("\u2502 ");
            for (var column = 0; column < widths.Count; column++)
            {
                var text = lineIndex < wrappedCells[column].Count ? wrappedCells[column][lineIndex] : string.Empty;
                var padded = TuiText.PadRightToWidth(text, widths[column]);
                row.Append(header ? _theme.Bold(padded) : padded);
                row.Append(column == widths.Count - 1 ? " \u2502" : " \u2502 ");
            }

            lines.Add(row.ToString());
        }
    }

    private static IReadOnlyList<int> FitColumnWidths(IReadOnlyList<int> naturalWidths, int availableForCells)
    {
        var widths = naturalWidths.Select(static width => Math.Max(1, width)).ToArray();
        var total = widths.Sum();
        if (total <= availableForCells)
        {
            return widths;
        }

        var columnCount = widths.Length;
        for (var i = 0; i < columnCount; i++)
        {
            widths[i] = Math.Max(1, availableForCells / columnCount);
        }

        var leftover = availableForCells - widths.Sum();
        for (var i = 0; leftover > 0 && i < widths.Length; i++, leftover--)
        {
            widths[i]++;
        }

        return widths;
    }

    private static string BorderLine(char left, char join, char right, IReadOnlyList<int> widths) =>
        left + "\u2500" + string.Join($"\u2500{join}\u2500", widths.Select(width => new string('\u2500', width))) + "\u2500" + right;

    private string CollectParagraph(IReadOnlyList<string> sourceLines, ref int index)
    {
        var paragraph = new List<string>();
        while (index < sourceLines.Count &&
            !string.IsNullOrWhiteSpace(sourceLines[index]) &&
            !IsBlockStart(sourceLines, index))
        {
            paragraph.Add(sourceLines[index].Trim());
            index++;
        }

        return string.Join(' ', paragraph);
    }

    private string RenderInline(string text, bool applyDefaultStyle = true)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var plain = new StringBuilder();

        for (var index = 0; index < text.Length;)
        {
            if (TryReadLink(text, index, out var linkText, out var href, out var linkLength))
            {
                FlushPlain();
                var renderedLinkText = RenderInline(linkText, applyDefaultStyle);
                var styledLink = _theme.Link(_theme.Underline(renderedLinkText));
                if (_enableHyperlinks)
                {
                    builder.Append(Hyperlink(styledLink, href));
                }
                else
                {
                    builder.Append(styledLink);
                    var hrefForComparison = href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? href[7..] : href;
                    if (!string.Equals(linkText, href, StringComparison.Ordinal) &&
                        !string.Equals(linkText, hrefForComparison, StringComparison.Ordinal))
                    {
                        builder.Append(_theme.LinkUrl($" ({href})"));
                    }
                }

                index += linkLength;
                continue;
            }

            if (TryReadDelimited(text, index, "`", allowWhitespaceEdges: true, out var code, out var codeLength))
            {
                FlushPlain();
                builder.Append(_theme.Code(code));
                index += codeLength;
                continue;
            }

            if (TryReadDelimited(text, index, "~~", allowWhitespaceEdges: false, out var deleted, out var deletedLength))
            {
                FlushPlain();
                builder.Append(_theme.Strikethrough(RenderInline(deleted, applyDefaultStyle)));
                index += deletedLength;
                continue;
            }

            if (TryReadDelimited(text, index, "**", allowWhitespaceEdges: false, out var strong, out var strongLength) ||
                TryReadDelimited(text, index, "__", allowWhitespaceEdges: false, out strong, out strongLength))
            {
                FlushPlain();
                builder.Append(_theme.Bold(RenderInline(strong, applyDefaultStyle)));
                index += strongLength;
                continue;
            }

            if (text[index] == '*' && (index + 1 >= text.Length || text[index + 1] != '*') &&
                TryReadDelimited(text, index, "*", allowWhitespaceEdges: false, out var emphasis, out var emphasisLength))
            {
                FlushPlain();
                builder.Append(_theme.Italic(RenderInline(emphasis, applyDefaultStyle)));
                index += emphasisLength;
                continue;
            }

            plain.Append(text[index]);
            index++;
        }

        FlushPlain();
        return builder.ToString();

        void FlushPlain()
        {
            if (plain.Length == 0)
            {
                return;
            }

            var value = plain.ToString();
            builder.Append(applyDefaultStyle ? ApplyDefaultStyle(value) : value);
            plain.Clear();
        }
    }

    private string ApplyDefaultStyle(string text)
    {
        if (_defaultTextStyle is not { } style)
        {
            return text;
        }

        var styled = style.Color is null ? text : style.Color(text);
        if (style.Bold)
        {
            styled = _theme.Bold(styled);
        }

        if (style.Italic)
        {
            styled = _theme.Italic(styled);
        }

        if (style.Strikethrough)
        {
            styled = _theme.Strikethrough(styled);
        }

        if (style.Underline)
        {
            styled = _theme.Underline(styled);
        }

        return styled;
    }

    private string ApplyBackground(string line, int width)
    {
        var padded = TuiText.PadRightToWidth(line, width);
        return _defaultTextStyle?.BackgroundColor is { } background
            ? background(padded)
            : padded;
    }

    private static bool TryReadLink(string text, int index, out string linkText, out string href, out int length)
    {
        linkText = string.Empty;
        href = string.Empty;
        length = 0;
        if (text[index] != '[')
        {
            return false;
        }

        var labelEnd = text.IndexOf("](", index, StringComparison.Ordinal);
        if (labelEnd < 0)
        {
            return false;
        }

        var hrefStart = labelEnd + 2;
        var hrefEnd = text.IndexOf(')', hrefStart);
        if (hrefEnd < 0)
        {
            return false;
        }

        linkText = text[(index + 1)..labelEnd];
        href = text[hrefStart..hrefEnd];
        length = hrefEnd - index + 1;
        return linkText.Length > 0 && href.Length > 0;
    }

    private static bool TryReadDelimited(
        string text,
        int index,
        string delimiter,
        bool allowWhitespaceEdges,
        out string content,
        out int length)
    {
        content = string.Empty;
        length = 0;
        if (!text.AsSpan(index).StartsWith(delimiter, StringComparison.Ordinal))
        {
            return false;
        }

        var contentStart = index + delimiter.Length;
        var close = text.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }

        content = text[contentStart..close];
        if (content.Length == 0)
        {
            return false;
        }

        if (!allowWhitespaceEdges && (char.IsWhiteSpace(content[0]) || char.IsWhiteSpace(content[^1])))
        {
            return false;
        }

        length = close - index + delimiter.Length;
        return true;
    }

    private static bool TryParseListMarker(string line, out ListMarker marker)
    {
        var ordered = OrderedListRegex.Match(line);
        if (ordered.Success)
        {
            marker = new ListMarker(
                Depth: ordered.Groups[1].Value.Length / 2,
                Bullet: ordered.Groups[2].Value + ". ",
                Text: ordered.Groups[3].Value);
            return true;
        }

        var unordered = UnorderedListRegex.Match(line);
        if (unordered.Success)
        {
            marker = new ListMarker(
                Depth: unordered.Groups[1].Value.Length / 2,
                Bullet: "- ",
                Text: unordered.Groups[2].Value);
            return true;
        }

        marker = default;
        return false;
    }

    private static bool IsBlockStart(IReadOnlyList<string> sourceLines, int index)
    {
        var line = sourceLines[index];
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) ||
            trimmed.StartsWith("~~~", StringComparison.Ordinal) ||
            trimmed.StartsWith('>') ||
            HeadingRegex.IsMatch(line) ||
            IsHorizontalRule(line) ||
            TryParseListMarker(line, out _) ||
            IsTableStart(sourceLines, index);
    }

    private static bool IsHorizontalRule(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 &&
            (trimmed.All(static c => c == '-') ||
             trimmed.All(static c => c == '*') ||
             trimmed.All(static c => c == '_'));
    }

    private static bool IsTableStart(IReadOnlyList<string> sourceLines, int index) =>
        index + 1 < sourceLines.Count &&
        TrySplitTableRow(sourceLines[index], out var headers) &&
        headers.Count > 0 &&
        TrySplitTableRow(sourceLines[index + 1], out var separators) &&
        separators.Count >= headers.Count &&
        separators.Take(headers.Count).All(IsTableSeparatorCell);

    private static bool TrySplitTableRow(string line, out IReadOnlyList<string> cells)
    {
        cells = [];
        if (!line.Contains('|', StringComparison.Ordinal))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        cells = trimmed.Split('|').Select(static cell => cell.Trim()).ToArray();
        return cells.Count > 0;
    }

    private static IReadOnlyList<string> NormalizeCellCount(IReadOnlyList<string> cells, int count)
    {
        if (cells.Count == count)
        {
            return cells;
        }

        var normalized = new string[count];
        for (var i = 0; i < count; i++)
        {
            normalized[i] = i < cells.Count ? cells[i] : string.Empty;
        }

        return normalized;
    }

    private static bool IsTableSeparatorCell(string cell)
    {
        var trimmed = cell.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        trimmed = trimmed.Trim(':');
        return trimmed.Length >= 3 && trimmed.All(static c => c == '-');
    }

    private static string Hyperlink(string text, string url) =>
        TuiTerminalImage.Hyperlink(text, url);

    private readonly record struct ListMarker(int Depth, string Bullet, string Text);
}
