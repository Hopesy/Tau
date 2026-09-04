using Spectre.Console;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

/// <summary>
/// 【终端启动】【品牌标识】生成 Tau 终端启动时显示的 Figlet 品牌大字。
/// </summary>
public static class TuiWelcomeBanner
{
    private const string BrandText = "Tau Code";
    private const int RenderWidth = 80;

    private static readonly IReadOnlyList<string> DefaultLines = CreateLines();

    /// <summary>
    /// 获取默认 Tau 品牌 banner 的文本行。
    /// </summary>
    public static IReadOnlyList<string> Lines => DefaultLines;

    /// <summary>
    /// 判断指定文本是否属于默认 Tau 品牌 banner，用于套用品牌样式。
    /// </summary>
    /// <param name="line">待判断的终端文本行。</param>
    /// <returns>属于品牌 banner 时返回 <see langword="true"/>。</returns>
    public static bool IsBannerLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        var normalized = line.TrimEnd();
        return DefaultLines.Any(bannerLine =>
            string.Equals(bannerLine, normalized, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> CreateLines()
    {
        // 1. 使用 Spectre Figlet 生成与 Cade 一致的大字字形
        var renderable = new FigletText(BrandText).LeftJustified();
        var rendered = new TuiSpectreRenderer(AnsiSupport.No).Render(renderable, RenderWidth);

        // 2. 去除渲染器为稳定宽度补出的尾随空格，保留字形左侧留白
        return rendered
            .Select(static line => line.TrimEnd())
            .Where(static line => line.Length > 0)
            .ToArray();
    }
}
