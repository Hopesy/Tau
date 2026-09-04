using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public readonly record struct TuiStatusBarLine(string Left, string Right);

/// <summary>
/// 定义状态栏左右段落的显示格式化函数。
/// </summary>
public sealed class TuiStatusBarTheme
{
    /// <summary>
    /// 创建状态栏主题。
    /// </summary>
    /// <param name="leftFormatter">左侧文本格式化函数。</param>
    /// <param name="rightFormatter">右侧文本格式化函数。</param>
    public TuiStatusBarTheme(
        Func<string, string>? leftFormatter = null,
        Func<string, string>? rightFormatter = null)
    {
        LeftFormatter = leftFormatter;
        RightFormatter = rightFormatter;
    }

    /// <summary>
    /// 不添加 ANSI 样式的默认主题。
    /// </summary>
    public static TuiStatusBarTheme Plain { get; } = new();

    /// <summary>
    /// 适合 Agent 交互界面的低对比度路径和高对比度模型主题。
    /// </summary>
    public static TuiStatusBarTheme Agent { get; } = new(
        leftFormatter: static value => $"\u001b[90m{value}\u001b[39m",
        rightFormatter: static value => $"\u001b[36m{value}\u001b[39m");

    /// <summary>左侧文本格式化函数。</summary>
    public Func<string, string>? LeftFormatter { get; }

    /// <summary>右侧文本格式化函数。</summary>
    public Func<string, string>? RightFormatter { get; }
}

public sealed class TuiStatusBar : ITuiComponent
{
    private readonly List<TuiStatusBarLine> _lines = [];
    private readonly TuiStatusBarTheme _theme;

    /// <summary>
    /// 创建状态栏。
    /// </summary>
    /// <param name="left">初始左侧文本。</param>
    /// <param name="right">初始右侧文本。</param>
    /// <param name="theme">状态栏样式主题。</param>
    public TuiStatusBar(
        string left = "",
        string right = "",
        TuiStatusBarTheme? theme = null)
    {
        _theme = theme ?? TuiStatusBarTheme.Plain;
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
        _lines.Select(line => RenderLine(line.Left, line.Right, width, _theme)).ToArray();

    public static string RenderLine(string? left, string? right, int width)
        => RenderLine(left, right, width, TuiStatusBarTheme.Plain);

    /// <summary>
    /// 按主题格式化状态栏左右文本，并保持最终可见宽度稳定。
    /// </summary>
    /// <param name="left">左侧状态文本。</param>
    /// <param name="right">右侧状态文本。</param>
    /// <param name="width">状态栏目标宽度。</param>
    /// <param name="theme">状态栏显示主题。</param>
    /// <returns>包含可选 ANSI 样式的状态栏行。</returns>
    private static string RenderLine(
        string? left,
        string? right,
        int width,
        TuiStatusBarTheme theme)
    {
        width = Math.Max(1, width);
        var leftText = TuiText.NormalizeSingleLine(left);
        var rightText = TuiText.NormalizeSingleLine(right);

        if (rightText.Length == 0)
        {
            var leftLine = TuiText.TruncateToWidth(leftText, width, string.Empty, pad: true);
            return theme.LeftFormatter?.Invoke(leftLine) ?? leftLine;
        }

        var rightRendered = TuiText.TruncateToWidth(rightText, width, string.Empty);
        var rightWidth = TuiText.VisibleWidth(rightRendered);
        if (leftText.Length == 0)
        {
            var rightLine = new string(' ', Math.Max(0, width - rightWidth)) + rightRendered;
            return theme.RightFormatter?.Invoke(rightLine) ?? rightLine;
        }

        if (rightWidth >= width)
        {
            var rightLine = TuiText.TruncateToWidth(rightRendered, width, string.Empty, pad: true);
            return theme.RightFormatter?.Invoke(rightLine) ?? rightLine;
        }

        var leftBudget = width - rightWidth - 1;
        if (leftBudget <= 0)
        {
            var rightLine = TuiText.TruncateToWidth(rightRendered, width, string.Empty, pad: true);
            return theme.RightFormatter?.Invoke(rightLine) ?? rightLine;
        }

        var leftRendered = TuiText.TruncateToWidth(leftText, leftBudget, string.Empty);
        var spaces = width - TuiText.VisibleWidth(leftRendered) - rightWidth;
        return (theme.LeftFormatter?.Invoke(leftRendered) ?? leftRendered) +
            new string(' ', Math.Max(0, spaces)) +
            (theme.RightFormatter?.Invoke(rightRendered) ?? rightRendered);
    }
}
