using System.Text;
using Spectre.Console;
using Spectre.Console.Json;
using Spectre.Console.Rendering;

namespace Tau.Tui.Rendering;

/// <summary>
/// 将 Spectre.Console 的 Renderable 内容转换为 Tau 可组合的 ANSI 行。
/// </summary>
public sealed class TuiSpectreRenderer
{
    /// <summary>
    /// 获取默认的 ANSI Spectre 渲染器。
    /// </summary>
    public static TuiSpectreRenderer Default { get; } = new();

    /// <summary>
    /// 使用指定的 Spectre 配置创建渲染适配器。
    /// </summary>
    /// <param name="ansi">可选的 Spectre ANSI 输出配置。</param>
    public TuiSpectreRenderer(AnsiSupport ansi = AnsiSupport.Yes)
    {
        Ansi = ansi;
    }

    /// <summary>
    /// 获取适配器使用的 ANSI 输出策略。
    /// </summary>
    public AnsiSupport Ansi { get; }

    /// <summary>
    /// 将 Spectre Renderable 渲染为指定宽度的终端行。
    /// </summary>
    /// <param name="renderable">需要渲染的 Spectre 内容。</param>
    /// <param name="width">目标终端列数。</param>
    /// <returns>已经按可见宽度裁剪并补齐的 ANSI 行。</returns>
    public IReadOnlyList<string> Render(IRenderable renderable, int width)
    {
        ArgumentNullException.ThrowIfNull(renderable);

        width = Math.Max(1, width);
        using var writer = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = Ansi,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });

        console.Profile.Width = width;
        console.Write(renderable);
        return NormalizeLines(writer.ToString(), width);
    }

    /// <summary>
    /// 使用 Spectre 的 Panel 组件渲染一块带标题的内容。
    /// </summary>
    /// <param name="title">Panel 标题。</param>
    /// <param name="content">Panel 内的纯文本内容。</param>
    /// <param name="width">目标终端列数。</param>
    /// <returns>已经按可见宽度裁剪并补齐的 Panel 行。</returns>
    public IReadOnlyList<string> RenderPanel(string title, string content, int width)
    {
        content ??= string.Empty;
        return Render(CreatePanel(title, new Text(content), content, width), width);
    }

    /// <summary>
    /// 使用 Spectre.Console.Json 渲染带语法颜色的 JSON Panel。
    /// </summary>
    /// <param name="title">Panel 标题。</param>
    /// <param name="json">需要渲染的合法 JSON 文本。</param>
    /// <param name="width">目标终端列数。</param>
    /// <returns>已经按可见宽度裁剪并补齐的 JSON Panel 行。</returns>
    public IReadOnlyList<string> RenderJsonPanel(string title, string json, int width)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return Render(CreatePanel(title, new JsonText(json), json, width), width);
    }

    /// <summary>
    /// 使用 Spectre Markup 渲染带颜色和装饰的文本。
    /// </summary>
    /// <param name="markup">Spectre Markup 文本。</param>
    /// <param name="width">目标终端列数。</param>
    /// <returns>已经按可见宽度裁剪并补齐的 ANSI 行。</returns>
    public IReadOnlyList<string> RenderMarkup(string markup, int width) =>
        Render(new Markup(markup ?? string.Empty), width);

    /// <summary>
    /// 创建带统一 Cade 风格标题、边框和内边距的 Spectre Panel。
    /// </summary>
    /// <param name="title">Panel 标题。</param>
    /// <param name="content">Panel 内容 Renderable。</param>
    /// <returns>配置完成的 Spectre Panel。</returns>
    /// <summary>
    /// 创建按标题和纯文本内容计算最小宽度的 Panel，避免空内容工具块退化为无标题小方框。
    /// </summary>
    /// <param name="title">Panel 标题。</param>
    /// <param name="content">Panel 内容 Renderable。</param>
    /// <param name="plainContent">用于估算宽度的纯文本内容。</param>
    /// <param name="width">目标终端列数。</param>
    /// <returns>配置完成的 Spectre Panel。</returns>
    private static Panel CreatePanel(string title, IRenderable content, string plainContent, int width)
    {
        var header = new PanelHeader(
            $" [bold yellow]{Markup.Escape(title ?? string.Empty)}[/] ",
            Justify.Left);
        var titleWidth = TuiText.VisibleWidth(title ?? string.Empty) + 4;
        var contentWidth = (plainContent ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(TuiText.VisibleWidth)
            .DefaultIfEmpty(0)
            .Max() + 4;
        var panelWidth = Math.Clamp(
            Math.Max(10, Math.Max(titleWidth, contentWidth)),
            1,
            Math.Max(1, width));

        return new Panel(content)
        {
            Header = header,
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            // 让面板按内容宽度收缩，避免每个工具调用都铺满整行并制造大块空白
            Expand = false,
            Width = panelWidth,
            Padding = new Padding(1, 0, 1, 0),
        };
    }

    /// <summary>
    /// 将 ANSI 输出按换行拆分，并统一为 Tau 的稳定可见宽度。
    /// </summary>
    /// <param name="text">Spectre 输出的 ANSI 文本。</param>
    /// <param name="width">目标终端列数。</param>
    /// <returns>稳定宽度的终端行集合。</returns>
    private static IReadOnlyList<string> NormalizeLines(string text, int width)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        if (lines.Length == 0)
        {
            return [TuiText.PadRightToWidth(string.Empty, width)];
        }

        return lines
            .Select(line => TuiText.PadRightToWidth(TuiText.TruncateToWidth(line, width, string.Empty), width))
            .ToArray();
    }
}
