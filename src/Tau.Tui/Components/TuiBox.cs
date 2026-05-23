using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public sealed class TuiBox : TuiContainer
{
    private readonly int _paddingX;
    private readonly int _paddingY;
    private RenderCache? _cache;

    public TuiBox(int paddingX = 1, int paddingY = 1)
    {
        _paddingX = Math.Max(0, paddingX);
        _paddingY = Math.Max(0, paddingY);
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

        if (_cache is not null && _cache.Width == width && _cache.ChildLines.SequenceEqual(childLines))
        {
            return _cache.Lines;
        }

        var lines = new List<string>();
        var emptyLine = new string(' ', width);
        for (var i = 0; i < _paddingY; i++)
        {
            lines.Add(emptyLine);
        }

        var left = new string(' ', _paddingX);
        foreach (var line in childLines)
        {
            lines.Add(TuiText.PadRightToWidth(left + line, width));
        }

        for (var i = 0; i < _paddingY; i++)
        {
            lines.Add(emptyLine);
        }

        _cache = new RenderCache(width, childLines.ToArray(), lines);
        return lines;
    }

    private sealed record RenderCache(int Width, IReadOnlyList<string> ChildLines, IReadOnlyList<string> Lines);
}
