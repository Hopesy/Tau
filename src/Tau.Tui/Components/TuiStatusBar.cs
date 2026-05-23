using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public sealed class TuiStatusBar : ITuiComponent
{
    private string _left;
    private string _right;

    public TuiStatusBar(string left = "", string right = "")
    {
        _left = left ?? string.Empty;
        _right = right ?? string.Empty;
    }

    public string Left => _left;
    public string Right => _right;

    public void SetSegments(string left, string right)
    {
        _left = left ?? string.Empty;
        _right = right ?? string.Empty;
    }

    public void Invalidate()
    {
    }

    public IReadOnlyList<string> Render(int width) => [RenderLine(_left, _right, width)];

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
