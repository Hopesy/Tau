using Tau.Tui.Abstractions;

namespace Tau.Tui.Components;

public sealed class TuiSpacer : ITuiComponent
{
    private int _lines;
    private IReadOnlyList<string>? _cachedLines;

    public TuiSpacer(int lines = 1)
    {
        _lines = lines;
    }

    public int Lines => _lines;

    public void SetLines(int lines)
    {
        if (_lines == lines)
        {
            return;
        }

        _lines = lines;
        Invalidate();
    }

    public void Invalidate() => _cachedLines = null;

    public IReadOnlyList<string> Render(int width)
    {
        var count = Math.Max(0, _lines);
        if (_cachedLines is not null && _cachedLines.Count == count)
        {
            return _cachedLines;
        }

        _cachedLines = count == 0
            ? Array.Empty<string>()
            : Enumerable.Repeat(string.Empty, count).ToArray();
        return _cachedLines;
    }
}
