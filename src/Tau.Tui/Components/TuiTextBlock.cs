using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public sealed class TuiTextBlock : ITuiComponent
{
    private readonly int _paddingX;
    private readonly int _paddingY;
    private readonly bool _wrap;
    private string _text;
    private string? _cachedText;
    private int? _cachedWidth;
    private IReadOnlyList<string>? _cachedLines;

    public TuiTextBlock(string text = "", int paddingX = 1, int paddingY = 1, bool wrap = true)
    {
        _text = text;
        _paddingX = Math.Max(0, paddingX);
        _paddingY = Math.Max(0, paddingY);
        _wrap = wrap;
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
            var empty = Array.Empty<string>();
            _cachedText = _text;
            _cachedWidth = width;
            _cachedLines = empty;
            return empty;
        }

        var contentWidth = Math.Max(1, width - (_paddingX * 2));
        var sourceLines = _wrap ? TuiText.Wrap(_text, contentWidth) : FirstLineOnly(_text);
        var lines = new List<string>();
        var emptyLine = new string(' ', width);

        for (var i = 0; i < _paddingY; i++)
        {
            lines.Add(emptyLine);
        }

        var left = new string(' ', _paddingX);
        var right = new string(' ', _paddingX);
        foreach (var sourceLine in sourceLines)
        {
            var rendered = _wrap
                ? sourceLine
                : TuiText.TruncateToWidth(sourceLine, contentWidth);
            lines.Add(TuiText.PadRightToWidth(left + rendered + right, width));
        }

        for (var i = 0; i < _paddingY; i++)
        {
            lines.Add(emptyLine);
        }

        _cachedText = _text;
        _cachedWidth = width;
        _cachedLines = lines;
        return lines;
    }

    private static IReadOnlyList<string> FirstLineOnly(string text)
    {
        var newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? [text] : [text[..newline]];
    }
}
