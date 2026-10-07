// 作者：xxx
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【MCP 管理菜单】携带标题、来源说明、可选操作和默认选择。</summary>
public sealed record CodingAgentMcpMenu(string Title, IReadOnlyList<TuiSelectItem> Items, string? Details = null, string? Selected = null);

/// <summary>【CodingAgent】【MCP 管理选择器】在连接状态变化时重建菜单并保留选择，支持控制台和组合界面。</summary>
public static class CodingAgentMcpMenuSelector
{
    /// <summary>【CodingAgent】【MCP 控制台菜单】使用现有键盘和 ANSI 画布承载动态管理菜单。</summary><param name="reader">键盘来源。</param><returns>菜单选择回调。</returns>
    public static Func<Func<CodingAgentMcpMenu>, CancellationToken, Task<string?>> CreateConsoleSelector(IConsoleKeyReader reader) => async (build, token) =>
    {
        var component = new MenuComponent(build); var host = new TuiOverlayHost(component, reader, TuiAnsiRenderSurface.ForConsole());
        host.Render(force: true);
        return await RunAsync(component, cancellation => host.ReadInputAsync(cancellation).AsTask(), () => host.Render(), token).ConfigureAwait(false);
    };

    /// <summary>【CodingAgent】【MCP 组合菜单】把动态菜单作为当前会话覆盖层，退出后恢复原会话画面。</summary><param name="session">组合会话。</param><returns>菜单选择回调。</returns>
    public static Func<Func<CodingAgentMcpMenu>, CancellationToken, Task<string?>> CreateCompositionSelector(TuiCompositionSession session) => async (build, token) =>
    {
        var component = new MenuComponent(build);
        var width = Math.Max(1, Math.Min(90, session.Viewport.Width));
        var handle = session.OpenOverlay(component, new TuiTranscriptOverlayOptions(Width: width, Row: 1, Column: Math.Max(0, (session.Viewport.Width - width) / 2)));
        try
        {
            if (!session.IsStarted) session.Start(); else session.Render(force: true);
            return await RunAsync(component, cancellation => session.ReadInputAsync(cancellation).AsTask(), () => session.Render(), token).ConfigureAwait(false);
        }
        finally { session.CloseOverlay(handle); }
    };

    /// <summary>【CodingAgent】【MCP 菜单循环】一次只读取一个键，等待期间每四分之一秒刷新连接状态。</summary>
    /// <param name="component">菜单组件。</param><param name="read">单次读取。</param><param name="render">重绘。</param><param name="token">取消。</param><returns>选择或取消时的空值。</returns>
    private static async Task<string?> RunAsync(MenuComponent component, Func<CancellationToken, Task> read, Action render, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? input = null;
        try
        {
            while (!component.Finished)
            {
                input ??= read(lifetime.Token);
                var tick = Task.Delay(250, lifetime.Token);
                if (await Task.WhenAny(input, tick).ConfigureAwait(false) == input)
                { await input.ConfigureAwait(false); input = null; }
                else await tick.ConfigureAwait(false);
                if (component.Refresh()) render();
            }
            return component.Value;
        }
        finally
        {
            lifetime.Cancel();
            if (input is not null) try { await input.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>【CodingAgent】【MCP 菜单组件】串行处理重建、输入和绘制，状态变化不丢失当前服务器选择。</summary>
    internal sealed class MenuComponent : ITuiInputComponent
    {
        private readonly Func<CodingAgentMcpMenu> _build;
        private readonly object _gate = new();
        private CodingAgentMcpMenu? _menu;
        private TuiSelectList _selector = new([]);
        private bool _finished;
        private string? _value;
        internal bool Finished { get { lock (_gate) return _finished; } }
        internal string? Value { get { lock (_gate) return _value; } }
        /// <summary>【CodingAgent】【MCP 菜单创建】首次读取当前服务状态。</summary><param name="build">状态生成器。</param>
        internal MenuComponent(Func<CodingAgentMcpMenu> build) { _build = build; Refresh(); }
        /// <summary>【CodingAgent】【MCP 菜单刷新】只在可见内容变化时替换列表，保留仍存在的选中项。</summary><returns>是否需要重绘。</returns>
        internal bool Refresh()
        {
            var menu = _build();
            lock (_gate)
            {
                if (_menu is not null && _menu.Title == menu.Title && _menu.Details == menu.Details && _menu.Items.SequenceEqual(menu.Items)) return false;
                var selected = _selector.SelectedItem?.Value ?? menu.Selected;
                _menu = menu;
                _selector = new(menu.Items, 12, layout: new(20, 36, "Enter select · Esc back"));
                var index = menu.Items.ToList().FindIndex(item => item.Value == selected);
                if (index >= 0) _selector.SetSelectedIndex(index);
                _selector.Selected += item => { _value = item.Value; _finished = true; };
                _selector.Cancelled += () => _finished = true;
                return true;
            }
        }
        /// <summary>【CodingAgent】【MCP 菜单绘制】标题和说明按宽度换行，空列表仍允许取消。</summary><param name="width">可用宽度。</param><returns>文本行。</returns>
        public IReadOnlyList<string> Render(int width)
        {
            lock (_gate)
            {
                var title = new TuiTextBlock(_menu!.Title, 0, 0).Render(width);
                var details = string.IsNullOrEmpty(_menu.Details) ? Array.Empty<string>() : new TuiTextBlock(_menu.Details, 0, 0).Render(width);
                return [.. title, .. details, "", .. _selector.Render(width)];
            }
        }
        /// <summary>【CodingAgent】【MCP 菜单失效】使选择列表下次重新绘制。</summary>
        public void Invalidate() { lock (_gate) _selector.Invalidate(); }
        /// <summary>【CodingAgent】【MCP 菜单输入】在组件锁中路由键盘事件。</summary><param name="key">按键。</param><returns>是否消费。</returns>
        public TuiInputResult HandleInput(ConsoleKeyInfo key) { lock (_gate) return _selector.HandleInput(key); }
    }
}
