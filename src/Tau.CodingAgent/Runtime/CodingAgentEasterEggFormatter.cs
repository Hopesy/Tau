using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>
/// 【CodingAgent】【彩蛋展示】渲染上游交互组件中的隐藏展示内容。
/// 当前实现提供文本/终端基线，动画与真实图片组件保真留给后续 TUI 组件切片。
/// </summary>
public static class CodingAgentEasterEggFormatter
{
    private const int ArminWidth = 31;
    private const int ArminHeight = 36;
    private const string EarendilBlogUrl = "https://mariozechner.at/posts/2026-04-08-ive-sold-out/";
    private const string DaxnutsLink = "https://mistral.ai/news/mistral-vibe-2-0";

    private static readonly byte[] ArminBits =
    [
        0xff, 0xff, 0xff, 0x7f, 0xff, 0xf0, 0xff, 0x7f, 0xff, 0xed, 0xff, 0x7f, 0xff, 0xdb, 0xff, 0x7f, 0xff, 0xb7, 0xff,
        0x7f, 0xff, 0x77, 0xfe, 0x7f, 0x3f, 0xf8, 0xfe, 0x7f, 0xdf, 0xff, 0xfe, 0x7f, 0xdf, 0x3f, 0xfc, 0x7f, 0x9f, 0xc3,
        0xfb, 0x7f, 0x6f, 0xfc, 0xf4, 0x7f, 0xf7, 0x0f, 0xf7, 0x7f, 0xf7, 0xff, 0xf7, 0x7f, 0xf7, 0xff, 0xe3, 0x7f, 0xf7,
        0x07, 0xe8, 0x7f, 0xef, 0xf8, 0x67, 0x70, 0x0f, 0xff, 0xbb, 0x6f, 0xf1, 0x00, 0xd0, 0x5b, 0xfd, 0x3f, 0xec, 0x53,
        0xc1, 0xff, 0xef, 0x57, 0x9f, 0xfd, 0xee, 0x5f, 0x9f, 0xfc, 0xae, 0x5f, 0x1f, 0x78, 0xac, 0x5f, 0x3f, 0x00, 0x50,
        0x6c, 0x7f, 0x00, 0xdc, 0x77, 0xff, 0xc0, 0x3f, 0x78, 0xff, 0x01, 0xf8, 0x7f, 0xff, 0x03, 0x9c, 0x78, 0xff, 0x07,
        0x8c, 0x7c, 0xff, 0x0f, 0xce, 0x78, 0xff, 0xff, 0xcf, 0x7f, 0xff, 0xff, 0xcf, 0x78, 0xff, 0xff, 0xdf, 0x78, 0xff,
        0xff, 0xdf, 0x7d, 0xff, 0xff, 0x3f, 0x7e, 0xff, 0xff, 0xff, 0x7f
    ];

    /// <summary>
    /// 渲染上游 <c>ArminComponent</c> 的最终帧文本。
    /// </summary>
    /// <param name="width">可用终端宽度；小于内容宽度时会裁剪。</param>
    /// <returns>用于写入 transcript 的自定义消息。</returns>
    public static CodingAgentDisplayedMessage FormatArminSaysHi(int width = 80) =>
        new(
            CodingAgentMessageDisplayFormatter.CustomKind,
            string.Join(Environment.NewLine, RenderArminLines(width)));

    /// <summary>
    /// 渲染上游 <c>EarendilAnnouncementComponent</c> 的文本公告基线。
    /// </summary>
    /// <param name="width">可用终端宽度；用于控制边框与行裁剪。</param>
    /// <returns>用于写入 transcript 的自定义消息。</returns>
    public static CodingAgentDisplayedMessage FormatEarendilAnnouncement(int width = 80) =>
        new(
            CodingAgentMessageDisplayFormatter.CustomKind,
            string.Join(
                Environment.NewLine,
                BoxLines(
                    [
                        "pi has joined Earendil",
                        string.Empty,
                        "Read the blog post:",
                        EarendilBlogUrl
                    ],
                    width)));

    /// <summary>
    /// 渲染上游 <c>DaxnutsComponent</c> 的文字提示基线。
    /// </summary>
    /// <param name="width">可用终端宽度；用于控制边框与行裁剪。</param>
    /// <returns>用于写入 transcript 的自定义消息。</returns>
    public static CodingAgentDisplayedMessage FormatDaxnuts(int width = 80) =>
        new(
            CodingAgentMessageDisplayFormatter.CustomKind,
            string.Join(
                Environment.NewLine,
                BoxLines(
                    [
                        "Free Kimi K2.5 via OpenCode Zen",
                        "\"Powered by daxnuts\"",
                        "@thdxr",
                        string.Empty,
                        "Try OpenCode",
                        DaxnutsLink
                    ],
                    width)));

    /// <summary>
    /// 判断当前模型选择是否应触发上游 daxnuts 彩蛋。
    /// </summary>
    /// <param name="model">当前选中的模型。</param>
    /// <param name="message">触发时返回的自定义显示消息。</param>
    /// <returns>命中 opencode + kimi-k2.5 规则时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static bool TryFormatDaxnuts(
        Model? model,
        out CodingAgentDisplayedMessage message)
    {
        if (model is not null &&
            model.Provider.Equals("opencode", StringComparison.Ordinal) &&
            model.Id.Contains("kimi-k2.5", StringComparison.OrdinalIgnoreCase))
        {
            message = FormatDaxnuts();
            return true;
        }

        message = default!;
        return false;
    }

    /// <summary>
    /// 从上游 XBM bitset 构造半块字符最终帧。
    /// </summary>
    /// <param name="width">可用终端宽度。</param>
    /// <returns>可直接写入终端的行集合。</returns>
    private static IReadOnlyList<string> RenderArminLines(int width)
    {
        var normalizedWidth = Math.Max(1, width);
        var displayHeight = (int)Math.Ceiling(ArminHeight / 2.0);
        var lines = new List<string>(displayHeight + 1);
        for (var row = 0; row < displayHeight; row++)
        {
            var chars = new char[ArminWidth];
            for (var x = 0; x < ArminWidth; x++)
            {
                chars[x] = GetArminChar(x, row);
            }

            lines.Add(PadOrClip(" " + new string(chars).TrimEnd(), normalizedWidth));
        }

        lines.Add(PadOrClip(" ARMIN SAYS HI", normalizedWidth));
        return lines;
    }

    /// <summary>
    /// 将两行 XBM 像素合并为一个半块字符。
    /// </summary>
    /// <param name="x">水平像素位置。</param>
    /// <param name="row">半块字符行号。</param>
    /// <returns>表示上下两个像素状态的块字符。</returns>
    private static char GetArminChar(int x, int row)
    {
        var upper = GetArminPixel(x, row * 2);
        var lower = GetArminPixel(x, row * 2 + 1);
        return (upper, lower) switch
        {
            (true, true) => '█',
            (true, false) => '▀',
            (false, true) => '▄',
            _ => ' '
        };
    }

    /// <summary>
    /// 读取上游 XBM 位图中的单个像素。
    /// </summary>
    /// <param name="x">水平像素位置。</param>
    /// <param name="y">垂直像素位置。</param>
    /// <returns>前景像素返回 <see langword="true"/>；背景或越界返回 <see langword="false"/>。</returns>
    private static bool GetArminPixel(int x, int y)
    {
        if (x < 0 || x >= ArminWidth || y < 0 || y >= ArminHeight)
        {
            return false;
        }

        // 1. XBM 每行按 8 像素分组并使用 LSB-first 编码
        // 2. 上游约定 bit=0 是前景、bit=1 是背景
        var bytesPerRow = (int)Math.Ceiling(ArminWidth / 8.0);
        var byteIndex = y * bytesPerRow + x / 8;
        if (byteIndex < 0 || byteIndex >= ArminBits.Length)
        {
            return false;
        }

        var bitIndex = x % 8;
        return ((ArminBits[byteIndex] >> bitIndex) & 1) == 0;
    }

    /// <summary>
    /// 使用简单文本边框渲染公告内容。
    /// </summary>
    /// <param name="content">需要放入边框的文本行。</param>
    /// <param name="width">目标终端宽度。</param>
    /// <returns>带边框的文本行。</returns>
    private static IReadOnlyList<string> BoxLines(
        IReadOnlyList<string> content,
        int width)
    {
        var normalizedWidth = Math.Max(8, width);
        var innerWidth = Math.Max(0, normalizedWidth - 4);
        var border = "+" + new string('-', normalizedWidth - 2) + "+";
        var lines = new List<string>(content.Count + 2) { border };
        foreach (var line in content)
        {
            lines.Add("| " + PadOrClip(line, innerWidth) + " |");
        }

        lines.Add(border);
        return lines;
    }

    /// <summary>
    /// 按字符数裁剪或右侧补空格，使行宽稳定。
    /// </summary>
    /// <param name="line">原始文本行。</param>
    /// <param name="width">目标字符宽度。</param>
    /// <returns>宽度稳定的文本行。</returns>
    private static string PadOrClip(string line, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        return line.Length > width
            ? line[..width]
            : line.PadRight(width);
    }
}
