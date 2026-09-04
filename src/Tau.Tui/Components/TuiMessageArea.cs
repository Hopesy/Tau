using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public enum TuiMessageRole
{
    User,
    Assistant,
    Thinking,
    System,
    Tool,
    BranchSummary,
    CompactionSummary,
    Custom,
    Skill,
    Error,
    Status,
}

/// <summary>
/// 定义消息区域的角色前缀和显示样式。
/// </summary>
public sealed class TuiMessageDisplayOptions
{
    private readonly IReadOnlyDictionary<TuiMessageRole, string> _prefixes;

    /// <summary>
    /// 创建消息显示配置。
    /// </summary>
    /// <param name="prefixes">按消息角色提供的可见前缀。</param>
    /// <param name="prefixFormatter">可选的前缀格式化函数。</param>
    /// <param name="renderMarkdown">是否对主要对话消息启用 Markdown 排版。</param>
    /// <param name="lineFormatter">可选的整行样式格式化函数。</param>
    /// <param name="thinkingPanelRenderer">可选的思考消息 Panel 渲染函数。</param>
    /// <param name="alignContentTopWhenFits">内容未填满视口时是否从顶部开始显示。</param>
    /// <param name="separateConversationTurns">是否在相邻对话轮次之间加入一个空行。</param>
    public TuiMessageDisplayOptions(
        IReadOnlyDictionary<TuiMessageRole, string>? prefixes = null,
        Func<TuiMessageRole, string, string>? prefixFormatter = null,
        bool renderMarkdown = false,
        Func<TuiMessageRole, string, string>? lineFormatter = null,
        Func<string, int, IReadOnlyList<string>>? thinkingPanelRenderer = null,
        bool alignContentTopWhenFits = false,
        bool separateConversationTurns = false)
    {
        _prefixes = prefixes is null
            ? new Dictionary<TuiMessageRole, string>()
            : new Dictionary<TuiMessageRole, string>(prefixes);
        PrefixFormatter = prefixFormatter;
        RenderMarkdown = renderMarkdown;
        LineFormatter = lineFormatter;
        ThinkingPanelRenderer = thinkingPanelRenderer;
        AlignContentTopWhenFits = alignContentTopWhenFits;
        SeparateConversationTurns = separateConversationTurns;
    }

    /// <summary>
    /// 保留现有的纯文本角色前缀，适用于日志和兼容输出。
    /// </summary>
    public static TuiMessageDisplayOptions Plain { get; } = new();

    /// <summary>
    /// 使用紧凑符号和 ANSI 颜色区分用户、助手、思考及工具消息。
    /// </summary>
    public static TuiMessageDisplayOptions Agent { get; } = new(
        new Dictionary<TuiMessageRole, string>
        {
            [TuiMessageRole.User] = "\u001b[32m→\u001b[39m ",
            [TuiMessageRole.Assistant] = "\u001b[38;5;81m◆\u001b[39m ",
            [TuiMessageRole.Thinking] = "\u001b[2;3;245m⋮\u001b[22;23;39m ",
            [TuiMessageRole.System] = string.Empty,
            [TuiMessageRole.Tool] = "\u001b[38;5;214m⚒\u001b[39m ",
            [TuiMessageRole.BranchSummary] = "\u001b[35m↳\u001b[39m ",
            [TuiMessageRole.CompactionSummary] = "\u001b[36m⟳\u001b[39m ",
            [TuiMessageRole.Custom] = "\u001b[35m◇\u001b[39m ",
            [TuiMessageRole.Skill] = "\u001b[35m✦\u001b[39m ",
            [TuiMessageRole.Error] = "\u001b[31m×\u001b[39m ",
            [TuiMessageRole.Status] = "\u001b[90m·\u001b[39m ",
        },
        renderMarkdown: true,
        lineFormatter: static (role, line) => role switch
        {
            TuiMessageRole.User => $"\u001b[1m{line}\u001b[22m",
            TuiMessageRole.Thinking => $"\u001b[3;90m{line}\u001b[23;39m",
            TuiMessageRole.System when TuiWelcomeBanner.IsBannerLine(line)
                => $"\u001b[1;38;5;215m{line}\u001b[22;39m",
            TuiMessageRole.System when line.TrimStart().StartsWith("Tau — Coding Agent", StringComparison.Ordinal)
                => $"\u001b[1;38;5;215m{line}\u001b[22;39m",
            TuiMessageRole.System => $"\u001b[2;90m{line}\u001b[22;39m",
            TuiMessageRole.Error => $"\u001b[31m{line}\u001b[39m",
            _ => line,
        },
        thinkingPanelRenderer: static (text, width) =>
            TuiSpectreRenderer.Default.RenderPanel("💭 思维链 (Reasoning)", text, width),
        alignContentTopWhenFits: true,
        separateConversationTurns: true);

    /// <summary>
    /// 获取前缀格式化函数；返回值必须保留与原始文本一致的可见宽度。
    /// </summary>
    public Func<TuiMessageRole, string, string>? PrefixFormatter { get; }

    /// <summary>
    /// 是否对用户、助手和思考消息使用 Markdown 排版。
    /// </summary>
    public bool RenderMarkdown { get; }

    /// <summary>
    /// 可选的整行样式格式化函数。
    /// </summary>
    public Func<TuiMessageRole, string, string>? LineFormatter { get; }

    /// <summary>
    /// 可选的思考消息 Panel 渲染函数。
    /// </summary>
    public Func<string, int, IReadOnlyList<string>>? ThinkingPanelRenderer { get; }

    /// <summary>
    /// 内容未填满视口时是否从顶部开始显示，适合启动欢迎区和短对话。
    /// </summary>
    public bool AlignContentTopWhenFits { get; }

    /// <summary>
    /// 是否在用户、助手、思考和工具等对话轮次之间加入空行。
    /// </summary>
    public bool SeparateConversationTurns { get; }

    /// <summary>
    /// 根据角色获取消息前缀。
    /// </summary>
    /// <param name="role">消息角色。</param>
    /// <returns>包含 ANSI 样式的消息前缀。</returns>
    public string PrefixFor(TuiMessageRole role)
    {
        var prefix = _prefixes.TryGetValue(role, out var value)
            ? value
            : role switch
            {
                TuiMessageRole.User => "you> ",
                TuiMessageRole.Assistant => "tau> ",
                TuiMessageRole.Thinking => "thinking> ",
                TuiMessageRole.System => "system> ",
                TuiMessageRole.Tool => "tool> ",
                TuiMessageRole.BranchSummary => "branch> ",
                TuiMessageRole.CompactionSummary => "compaction> ",
                TuiMessageRole.Custom => "custom> ",
                TuiMessageRole.Skill => "skill> ",
                TuiMessageRole.Error => "error> ",
                TuiMessageRole.Status => "status> ",
                _ => "msg> ",
            };

        return PrefixFormatter?.Invoke(role, prefix) ?? prefix;
    }
}

public sealed record TuiMessage(TuiMessageRole Role, string Text);

public sealed class TuiMessageArea : ITuiComponent
{
    private readonly List<TuiMessage> _messages;
    private readonly int? _maxVisibleLines;
    private readonly TuiMessageDisplayOptions _displayOptions;

    /// <summary>
    /// 创建消息区域。
    /// </summary>
    /// <param name="messages">初始消息集合。</param>
    /// <param name="maxVisibleLines">可选的最大可见行数。</param>
    /// <param name="displayOptions">消息角色前缀和样式配置。</param>
    public TuiMessageArea(
        IEnumerable<TuiMessage>? messages = null,
        int? maxVisibleLines = null,
        TuiMessageDisplayOptions? displayOptions = null)
    {
        _messages = messages?.ToList() ?? [];
        _maxVisibleLines = maxVisibleLines is null ? null : Math.Max(1, maxVisibleLines.Value);
        _displayOptions = displayOptions ?? TuiMessageDisplayOptions.Plain;
    }

    public IReadOnlyList<TuiMessage> Messages => _messages;
    public int? MaxVisibleLines => _maxVisibleLines;

    public void Add(TuiMessage message) => _messages.Add(message);

    public void SetMessages(IEnumerable<TuiMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages.Clear();
        _messages.AddRange(messages);
    }

    public void Clear() => _messages.Clear();

    public void Invalidate()
    {
    }

    public IReadOnlyList<string> Render(int width) =>
        RenderMessages(_messages, width, _maxVisibleLines, _displayOptions);

    /// <summary>
    /// 将消息集合按指定宽度和显示配置渲染为终端行。
    /// </summary>
    /// <param name="messages">需要渲染的消息集合。</param>
    /// <param name="width">终端可用列数。</param>
    /// <param name="maxVisibleLines">可选的最大可见行数。</param>
    /// <param name="displayOptions">消息角色前缀和样式配置。</param>
    /// <returns>已经换行、裁剪并应用样式的终端行。</returns>
    public static IReadOnlyList<string> RenderMessages(
        IEnumerable<TuiMessage> messages,
        int width,
        int? maxVisibleLines = null,
        TuiMessageDisplayOptions? displayOptions = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        width = Math.Max(1, width);
        displayOptions ??= TuiMessageDisplayOptions.Plain;
        var lines = new List<string>();
        TuiMessageRole? previousRole = null;
        foreach (var message in messages)
        {
            if (displayOptions.SeparateConversationTurns &&
                previousRole is { } previous &&
                ShouldSeparateConversationTurns(previous, message.Role))
            {
                lines.Add(new string(' ', width));
            }

            AddMessageLines(lines, message, width, displayOptions);
            previousRole = message.Role;
        }

        if (maxVisibleLines is not { } requestedVisibleLines || lines.Count <= requestedVisibleLines)
        {
            return lines;
        }

        var visibleLines = Math.Max(1, requestedVisibleLines);
        return lines.Skip(lines.Count - visibleLines).ToArray();
    }

    private static void AddMessageLines(
        List<string> output,
        TuiMessage message,
        int width,
        TuiMessageDisplayOptions displayOptions)
    {
        var text = message.Text ?? string.Empty;

        if (message.Role == TuiMessageRole.Thinking && displayOptions.ThinkingPanelRenderer is { } panelRenderer)
        {
            output.AddRange(panelRenderer(text, width));
            return;
        }

        var prefix = displayOptions.PrefixFor(message.Role);
        var prefixWidth = TuiText.VisibleWidth(prefix);

        if (width <= prefixWidth)
        {
            foreach (var line in TuiText.Wrap(prefix + text, width))
            {
                AddFormattedLine(output, message.Role, line, width, displayOptions);
            }

            return;
        }

        var contentWidth = Math.Max(1, width - prefixWidth);

        if (displayOptions.RenderMarkdown &&
            message.Role is TuiMessageRole.User or TuiMessageRole.Assistant or TuiMessageRole.Thinking)
        {
            var markdownLines = new TuiMarkdown(text).Render(contentWidth);
            for (var i = 0; i < markdownLines.Count; i++)
            {
                var lead = i == 0 ? prefix : new string(' ', prefixWidth);
                AddFormattedLine(output, message.Role, lead + markdownLines[i], width, displayOptions);
            }

            return;
        }

        var wrapped = TuiText.Wrap(text, contentWidth);
        for (var i = 0; i < wrapped.Count; i++)
        {
            var lead = i == 0 ? prefix : new string(' ', prefixWidth);
            AddFormattedLine(output, message.Role, lead + wrapped[i], width, displayOptions);
        }
    }

    /// <summary>
    /// 判断两个消息角色之间是否需要视觉分隔。
    /// </summary>
    /// <param name="previous">前一条消息角色。</param>
    /// <param name="current">当前消息角色。</param>
    /// <returns>需要分隔时返回 <see langword="true"/>。</returns>
    private static bool ShouldSeparateConversationTurns(
        TuiMessageRole previous,
        TuiMessageRole current)
    {
        static bool IsConversationRole(TuiMessageRole role) => role is
            TuiMessageRole.User or
            TuiMessageRole.Assistant or
            TuiMessageRole.Thinking or
            TuiMessageRole.Tool or
            TuiMessageRole.Error;

        // 只在新一轮用户输入前分隔，避免工具流和助手流被空行拆散
        return current == TuiMessageRole.User && IsConversationRole(previous);
    }

    /// <summary>
    /// 将单行裁剪到稳定宽度并应用对应角色的整行样式。
    /// </summary>
    /// <param name="output">接收渲染结果的行集合。</param>
    /// <param name="role">当前消息角色。</param>
    /// <param name="line">待处理的终端文本。</param>
    /// <param name="width">目标可见宽度。</param>
    /// <param name="displayOptions">消息显示配置。</param>
    private static void AddFormattedLine(
        List<string> output,
        TuiMessageRole role,
        string line,
        int width,
        TuiMessageDisplayOptions displayOptions)
    {
        var rendered = TuiText.TruncateToWidth(line, width, string.Empty, pad: true);
        output.Add(displayOptions.LineFormatter?.Invoke(role, rendered) ?? rendered);
    }

}
