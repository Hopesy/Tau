using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public readonly record struct TuiStatusBarLine(string Left, string Right);

public sealed class TuiStatusBar : ITuiComponent
{
    private readonly List<TuiStatusBarLine> _lines = [];

    public TuiStatusBar(string left = "", string right = "")
    {
        SetSegments(left, right);
    }

    public string Left => _lines.Count == 0 ? string.Empty : _lines[0].Left;
    public string Right => _lines.Count == 0 ? string.Empty : _lines[0].Right;
    public IReadOnlyList<TuiStatusBarLine> Lines => _lines;
    public int LineCount => _lines.Count;

    public void SetSegments(string left, string right)
    {
        SetLines([new TuiStatusBarLine(left ?? string.Empty, right ?? string.Empty)]);
    }

    public void SetLines(IEnumerable<TuiStatusBarLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        _lines.Clear();
        foreach (var line in lines)
        {
            _lines.Add(new TuiStatusBarLine(
                TuiText.NormalizeSingleLine(line.Left),
                TuiText.NormalizeSingleLine(line.Right)));
        }

        if (_lines.Count == 0)
        {
            _lines.Add(new TuiStatusBarLine(string.Empty, string.Empty));
        }
    }

    public void Invalidate()
    {
    }

    public IReadOnlyList<string> Render(int width) =>
        _lines.Select(line => RenderLine(line.Left, line.Right, width)).ToArray();

    public static string RenderLine(string? left, string? right, int width)
    {
        width = Math.Max(1, width);
        var leftText = TuiText.NormalizeSingleLine(left);
        var rightText = TuiText.NormalizeSingleLine(right);

        if (rightText.Length == 0)
        {
            return TuiText.TruncateToWidth(leftText, width, string.Empty, pad: true);
        }

        var rightRendered = TuiText.TruncateToWidth(rightText, width, string.Empty);
        var rightWidth = TuiText.VisibleWidth(rightRendered);
        if (leftText.Length == 0)
        {
            return new string(' ', Math.Max(0, width - rightWidth)) + rightRendered;
        }

        if (rightWidth >= width)
        {
            return TuiText.TruncateToWidth(rightRendered, width, string.Empty, pad: true);
        }

        var leftBudget = width - rightWidth - 1;
        if (leftBudget <= 0)
        {
            return TuiText.TruncateToWidth(rightRendered, width, string.Empty, pad: true);
        }

        var leftRendered = TuiText.TruncateToWidth(leftText, leftBudget, string.Empty);
        var spaces = width - TuiText.VisibleWidth(leftRendered) - rightWidth;
        return leftRendered + new string(' ', Math.Max(0, spaces)) + rightRendered;
    }
}
