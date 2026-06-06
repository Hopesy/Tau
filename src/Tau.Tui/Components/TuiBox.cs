using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public sealed class TuiBox : TuiContainer
{
    private readonly int _paddingX;
    private readonly int _paddingY;
    private Func<string, string>? _backgroundFormatter;
    private RenderCache? _cache;

    public TuiBox(int paddingX = 1, int paddingY = 1, Func<string, string>? backgroundFormatter = null)
    {
        _paddingX = Math.Max(0, paddingX);
        _paddingY = Math.Max(0, paddingY);
        _backgroundFormatter = backgroundFormatter;
    }

    public void SetBackgroundFormatter(Func<string, string>? backgroundFormatter)
    {
        _backgroundFormatter = backgroundFormatter;
    }

    public override void Invalidate()
    {
        _cache = null;
        base.Invalidate();
    }

    public override IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width);
        if (Children.Count == 0)
        {
            return Array.Empty<string>();
        }

        var contentWidth = Math.Max(1, width - (_paddingX * 2));
        var childLines = new List<string>();
        foreach (var child in Children)
        {
            childLines.AddRange(child.Render(contentWidth));
        }

        if (childLines.Count == 0)
        {
            return Array.Empty<string>();
        }

        var backgroundSample = _backgroundFormatter?.Invoke("test");
        if (_cache is not null &&
            _cache.Width == width &&
            string.Equals(_cache.BackgroundSample, backgroundSample, StringComparison.Ordinal) &&
            _cache.ChildLines.SequenceEqual(childLines))
        {
            return _cache.Lines;
        }

        var lines = new List<string>();
        var emptyLine = FormatLine(string.Empty, width);
        for (var i = 0; i < _paddingY; i++)
        {
            lines.Add(emptyLine);
        }

        var left = new string(' ', _paddingX);
        foreach (var line in childLines)
        {
            lines.Add(FormatLine(left + line, width));
        }

        for (var i = 0; i < _paddingY; i++)
        {
            lines.Add(emptyLine);
        }

        _cache = new RenderCache(width, backgroundSample, childLines.ToArray(), lines);
        return lines;
    }

    private string FormatLine(string line, int width) =>
        _backgroundFormatter is { } formatter
            ? TuiText.ApplyBackgroundToLine(line, width, formatter)
            : TuiText.PadRightToWidth(line, width);

    private sealed record RenderCache(
        int Width,
        string? BackgroundSample,
        IReadOnlyList<string> ChildLines,
        IReadOnlyList<string> Lines);
}
