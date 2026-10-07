// 作者：xxx
using System.Globalization;
using Tau.Ai.Auth.OAuth;
using Tau.Tui;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【登录方式选择】承载提供方内部的选择提示，不改变提供方或认证方式注册表。</summary>
internal static class CodingAgentOAuthPromptSelector
{
    /// <summary>【CodingAgent】【控制台选择】创建复用主输入设备及当前主题的覆盖层选择回调。</summary>
    /// <param name="keys">按键来源。</param><param name="theme">主题来源。</param><param name="keybindings">当前快捷键配置。</param><param name="onToggleToolsExpanded">工具展开回调。</param><returns>交互回调。</returns>
    internal static Func<string, IReadOnlyList<OAuthSelectOption>, CancellationToken, Task<string?>> CreateConsoleSelector(
        IConsoleKeyReader keys, Func<CodingAgentTheme?>? theme = null, Func<TuiKeybindingsManager?>? keybindings = null, Action? onToggleToolsExpanded = null) =>
        (message, options, token) => SelectAsync(message, options, keys, TuiAnsiRenderSurface.ForConsole(), token, theme?.Invoke(), keybindings?.Invoke(), onToggleToolsExpanded);

    /// <summary>【CodingAgent】【组合选择】关闭或取消时移除覆盖层，恢复原输入区焦点。</summary>
    /// <param name="session">组合终端。</param><param name="theme">主题来源。</param><param name="keybindings">当前快捷键配置。</param><param name="onToggleToolsExpanded">工具展开回调。</param><returns>交互回调。</returns>
    internal static Func<string, IReadOnlyList<OAuthSelectOption>, CancellationToken, Task<string?>> CreateCompositionSelector(
        TuiCompositionSession session, Func<CodingAgentTheme?>? theme = null, Func<TuiKeybindingsManager?>? keybindings = null, Action? onToggleToolsExpanded = null) => async (message, options, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (options.Count == 0) return null;
        var component = new Component(message, options, theme?.Invoke(), keybindings?.Invoke(), onToggleToolsExpanded);
        var width = Math.Max(1, Math.Min(80, session.Viewport.Width));
        var handle = session.OpenOverlay(component, new TuiTranscriptOverlayOptions(Width: width,
            Row: Math.Max(0, Math.Min(1, session.Viewport.MessageHeight - 1)), Column: Math.Max(0, (session.Viewport.Width - width) / 2)));
        try
        {
            return await RunAsync(component, () => { if (session.IsStarted) session.Render(force: true); else session.Start(); },
                async signal => { await session.ReadInputAsync(signal).ConfigureAwait(false); }, token).ConfigureAwait(false);
        }
        finally { session.CloseOverlay(handle); }
    };

    /// <summary>【CodingAgent】【方式选择】在指定渲染目标运行方向键、默认选择和取消。</summary>
    /// <param name="message">标题。</param><param name="options">候选项。</param><param name="keys">按键。</param><param name="surface">渲染目标。</param>
    /// <param name="token">取消。</param><param name="theme">当前主题。</param><param name="keybindings">动作快捷键。</param><param name="onToggleToolsExpanded">工具展开回调。</param><returns>候选 ID 或取消空值。</returns>
    internal static Task<string?> SelectAsync(string message, IReadOnlyList<OAuthSelectOption> options,
        IConsoleKeyReader keys, ITuiRenderSurface surface, CancellationToken token, CodingAgentTheme? theme = null, TuiKeybindingsManager? keybindings = null, Action? onToggleToolsExpanded = null)
    {
        token.ThrowIfCancellationRequested();
        if (options.Count == 0) return Task.FromResult<string?>(null);
        var component = new Component(message, options, theme, keybindings, onToggleToolsExpanded);
        var host = new TuiOverlayHost(component, keys, surface);
        return RunAsync(component, () => host.Render(force: true), async signal => { await host.ReadInputAsync(signal).ConfigureAwait(false); }, token);
    }

    /// <summary>【CodingAgent】【选择生命周期】用户选择或取消后退出，外部取消不会留下焦点状态。</summary>
    /// <param name="component">选择组件。</param><param name="render">初始绘制。</param><param name="read">一次输入处理。</param><param name="token">取消。</param><returns>选择结果。</returns>
    private static async Task<string?> RunAsync(Component component, Action render, Func<CancellationToken, Task> read, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested(); render();
            while (component.Selection is null && !component.Cancelled) await read(token).ConfigureAwait(false);
            return component.Cancelled ? null : component.Selection;
        }
        finally { component.Focused = false; }
    }

    /// <summary>【CodingAgent】【选择组件】显示标题、标签和说明，边界导航不循环。</summary>
    /// <param name="message">标题。</param><param name="options">按提供方原顺序排列的选项。</param><param name="theme">主题。</param><param name="keybindings">动作快捷键。</param><param name="onToggleToolsExpanded">工具展开回调。</param>
    internal sealed class Component(string message, IReadOnlyList<OAuthSelectOption> options, CodingAgentTheme? theme = null,
        TuiKeybindingsManager? keybindings = null, Action? onToggleToolsExpanded = null) : ITuiInputComponent, Focusable
    {
        private readonly TuiKeybindingsManager _keybindings = keybindings ?? new CodingAgentKeybindings();
        public bool Focused { get; set; } = true;
        public int SelectedIndex { get; private set; }
        public string? Selection { get; private set; }
        public bool Cancelled { get; private set; }

        /// <summary>【CodingAgent】【方式导航】按上游优先级匹配可配置动作，保留 j/k 和换行的直接输入兼容。</summary>
        /// <param name="key">按键。</param><returns>是否处理。</returns>
        public TuiInputResult HandleInput(ConsoleKeyInfo key)
        {
            if (_keybindings.Matches(key, "app.tools.expand")) onToggleToolsExpanded?.Invoke();
            else if (_keybindings.Matches(key, "tui.select.up") || key.Modifiers == 0 && key.KeyChar == 'k') SelectedIndex = Math.Max(0, SelectedIndex - 1);
            else if (_keybindings.Matches(key, "tui.select.down") || key.Modifiers == 0 && key.KeyChar == 'j') SelectedIndex = Math.Max(0, Math.Min(options.Count - 1, SelectedIndex + 1));
            else if (_keybindings.Matches(key, "tui.select.confirm") || key.KeyChar == '\n') { if (options.Count > 0) Selection = options[SelectedIndex].Id; }
            else if (_keybindings.Matches(key, "tui.select.cancel")) Cancelled = true;
            else return TuiInputResult.Ignored;
            return TuiInputResult.Handled;
        }

        /// <summary>【CodingAgent】【方式渲染】按终端宽度换行并保留标签与说明，窄终端不越界。</summary>
        /// <param name="width">列数。</param><returns>ANSI 渲染行。</returns>
        public IReadOnlyList<string> Render(int width)
        {
            width = Math.Max(1, width);
            var lines = new List<string> { Color("border", new string('─', width)), "" };
            lines.AddRange(TuiText.Wrap(Color("accent", message), width)); lines.Add("");
            for (var index = 0; index < options.Count; index++)
            {
                var option = options[index];
                lines.AddRange(TuiText.Wrap(Color(index == SelectedIndex ? "accent" : "text", (index == SelectedIndex ? "→ " : "  ") + option.Label), width));
                if (option.Description is { Length: > 0 } detail) lines.AddRange(TuiText.Wrap(Color("muted", "  " + detail), width));
            }
            lines.Add(""); lines.AddRange(TuiText.Wrap(Color("muted", $"↑↓ navigate  {string.Join('/', _keybindings.GetKeys("tui.select.confirm"))} select  {string.Join('/', _keybindings.GetKeys("tui.select.cancel"))} cancel"), width));
            lines.Add(""); lines.Add(Color("border", new string('─', width)));
            return lines;
        }

        /// <summary>【CodingAgent】【选择主题】转换当前主题颜色；不支持的颜色保持纯文本。</summary>
        /// <param name="role">主题用途。</param><param name="text">内容。</param><returns>ANSI 内容。</returns>
        private string Color(string role, string text)
        {
            if (theme?.Colors.GetValueOrDefault(role) is not { Length: > 0 } color) return text;
            if (color.Length == 7 && color[0] == '#' && int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return $"\u001b[38;2;{rgb >> 16};{(rgb >> 8) & 255};{rgb & 255}m{text}\u001b[39m";
            return int.TryParse(color, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index is >= 0 and <= 255
                ? $"\u001b[38;5;{index}m{text}\u001b[39m" : text;
        }

        /// <summary>【CodingAgent】【选择重绘】没有缓存，每次按当前选择直接生成帧。</summary>
        public void Invalidate() { }
    }
}
