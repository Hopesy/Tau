namespace Tau.Tui.Runtime;

public sealed class TuiScrollbackBuffer
{
    private readonly List<string> _lines = [];
    private readonly int _maxLines;
    private int _height;
    private int _scrollOffsetFromBottom;

    public TuiScrollbackBuffer(int height, int maxLines = 10_000)
    {
        _height = Math.Max(1, height);
        _maxLines = Math.Max(1, maxLines);
    }

    public int Count => _lines.Count;
    public int Height => _height;
    public int MaxLines => _maxLines;
    public int ScrollOffsetFromBottom => _scrollOffsetFromBottom;
    public bool IsFollowingBottom => _scrollOffsetFromBottom == 0;

    public IReadOnlyList<string> Lines => _lines.AsReadOnly();

    public void Clear()
    {
        _lines.Clear();
        _scrollOffsetFromBottom = 0;
    }

    public void SetHeight(int height)
    {
        _height = Math.Max(1, height);
        ClampScrollOffset();
    }

    public void Append(string line) => Append([line]);

    public void Append(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var wasFollowingBottom = IsFollowingBottom;
        var added = 0;
        foreach (var line in lines)
        {
            _lines.Add(NormalizeLine(line));
            added++;
        }

        TrimOverflow();
        if (wasFollowingBottom)
        {
            _scrollOffsetFromBottom = 0;
        }
        else
        {
            _scrollOffsetFromBottom += added;
        }

        ClampScrollOffset();
    }

    public void Replace(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        _lines.Clear();
        foreach (var line in lines)
        {
            _lines.Add(NormalizeLine(line));
        }

        TrimOverflow();
        _scrollOffsetFromBottom = 0;
    }

    public void ScrollUp(int lines = 1)
    {
        if (lines <= 0)
        {
            return;
        }

        _scrollOffsetFromBottom += lines;
        ClampScrollOffset();
    }

    public void ScrollDown(int lines = 1)
    {
        if (lines <= 0)
        {
            return;
        }

        _scrollOffsetFromBottom -= lines;
        ClampScrollOffset();
    }

    public void PageUp() => ScrollUp(_height);

    public void PageDown() => ScrollDown(_height);

    public void ScrollToTop() => _scrollOffsetFromBottom = MaxScrollOffset();

    public void ScrollToBottom() => _scrollOffsetFromBottom = 0;

    public IReadOnlyList<string> VisibleLines()
    {
        if (_lines.Count == 0)
        {
            return [];
        }

        var visibleCount = Math.Min(_height, _lines.Count);
        var endExclusive = Math.Max(visibleCount, _lines.Count - _scrollOffsetFromBottom);
        var start = Math.Max(0, endExclusive - visibleCount);
        return _lines.Skip(start).Take(visibleCount).ToArray();
    }

    private void TrimOverflow()
    {
        var overflow = _lines.Count - _maxLines;
        if (overflow <= 0)
        {
            return;
        }

        _lines.RemoveRange(0, overflow);
        _scrollOffsetFromBottom = Math.Max(0, _scrollOffsetFromBottom - overflow);
    }

    private void ClampScrollOffset()
    {
        _scrollOffsetFromBottom = Math.Clamp(_scrollOffsetFromBottom, 0, MaxScrollOffset());
    }

    private int MaxScrollOffset() => Math.Max(0, _lines.Count - Math.Min(_height, _lines.Count));

    private static string NormalizeLine(string? line) =>
        (line ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ');
}
