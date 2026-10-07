using Tau.Ai.Auth;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentAuthSelectorState(
    string CurrentProvider,
    IReadOnlyList<ProviderAuthStatus> Providers)
{
    /// <summary>包含认证方式的选项，空集合保留旧状态选择器行为。</summary>
    public IReadOnlyList<CodingAgentAuthOption> Options { get; init; } = [];
    /// <summary>当前选择器动作。</summary>
    public string Mode { get; init; } = "auth";
    /// <summary>可选的初始搜索文本。</summary>
    public string? InitialFilter { get; init; }
    /// <summary>登录时是否先选择账户或 API key 方式。</summary>
    public bool SelectMethodFirst { get; init; }
    /// <summary>可选标题，例如指定提供方的认证方式菜单。</summary>
    public string? Title { get; init; }
}

public static partial class CodingAgentAuthSelector
{
    /// <summary>【CodingAgent】【认证选择】创建使用独立终端覆盖层的搜索选择器。</summary>
    /// <param name="keyReader">按键来源。</param><param name="synchronizedOutput">是否同步输出。</param><param name="themeProvider">每次打开时读取当前主题。</param>
    /// <returns>认证选择回调。</returns>
    public static Func<CodingAgentAuthSelectorState, CancellationToken, Task<string?>> CreateConsoleSelector(
        IConsoleKeyReader keyReader,
        bool synchronizedOutput = true,
        Func<CodingAgentTheme?>? themeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(keyReader);

        return (state, cancellationToken) => SelectAsync(
            state,
            keyReader,
            TuiAnsiRenderSurface.ForConsole(synchronizedOutput),
            cancellationToken, themeProvider?.Invoke());
    }

    /// <summary>【CodingAgent】【认证选择】创建复用组合终端和当前主题的搜索选择器。</summary>
    /// <param name="session">终端会话。</param><param name="themeProvider">当前主题来源。</param><returns>认证选择回调。</returns>
    public static Func<CodingAgentAuthSelectorState, CancellationToken, Task<string?>> CreateCompositionSelector(
        TuiCompositionSession session, Func<CodingAgentTheme?>? themeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        return (state, cancellationToken) => SelectFlowAsync(state, async panelState =>
        {
            var selector = new CodingAgentAuthSelectorComponent(panelState, theme: themeProvider?.Invoke());
            var width = Math.Max(1, Math.Min(80, session.Viewport.Width));
            var handle = session.OpenOverlay(selector, new TuiTranscriptOverlayOptions(Width: width,
                Row: Math.Max(0, Math.Min(1, session.Viewport.MessageHeight - 1)), Column: Math.Max(0, (session.Viewport.Width - width) / 2)));
            try
            {
                return await RunSelectionAsync(selector,
                    () => { if (session.IsStarted) session.Render(force: true); else session.Start(); },
                    async token => { await session.ReadInputAsync(token).ConfigureAwait(false); }, cancellationToken).ConfigureAwait(false);
            }
            finally { session.CloseOverlay(handle); }
        }, cancellationToken);
    }

    /// <summary>【CodingAgent】【认证选择】创建认证方式列表，兼容旧提供方状态列表。</summary>
    /// <param name="state">当前提供方及选项。</param><param name="maxVisible">最多显示的条目数。</param><returns>可交互列表。</returns>
    public static TuiSelectList CreateSelectList(
        CodingAgentAuthSelectorState state,
        int maxVisible = 8)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Options.Count > 0) return CreateOptionsList(state, maxVisible);

        var items = state.Providers
            .Select(static status => new TuiSelectItem(
                status.Provider,
                CodingAgentProviderDisplayNames.Resolve(status.Provider),
                FormatDescription(status)))
            .ToArray();
        var selector = new TuiSelectList(
            items,
            maxVisible: maxVisible,
            layout: new TuiSelectListLayout(MinPrimaryColumnWidth: 18, MaxPrimaryColumnWidth: 32));
        var index = Array.FindIndex(items, item => item.Value.Equals(state.CurrentProvider, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            selector.SetSelectedIndex(index);
        }

        return selector;
    }

    /// <summary>【CodingAgent】【认证选择】在指定终端运行搜索、选择与取消循环。</summary>
    /// <param name="state">认证状态。</param><param name="keyReader">按键来源。</param><param name="surface">渲染目标。</param>
    /// <param name="cancellationToken">取消信号。</param><param name="theme">可选主题。</param><returns>完整选择值；取消为空。</returns>
    public static async Task<string?> SelectAsync(
        CodingAgentAuthSelectorState state,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        CancellationToken cancellationToken = default, CodingAgentTheme? theme = null)
    {
        ArgumentNullException.ThrowIfNull(keyReader);
        ArgumentNullException.ThrowIfNull(surface);

        return await SelectFlowAsync(state, panelState =>
        {
            var selector = new CodingAgentAuthSelectorComponent(panelState, theme: theme);
            var host = new TuiOverlayHost(selector, keyReader, surface);
            return RunSelectionAsync(selector, () => host.Render(force: true),
                async token => { await host.ReadInputAsync(token).ConfigureAwait(false); }, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【认证交互】共用选择生命周期，异常和取消时释放事件及输入焦点。</summary>
    /// <param name="selector">搜索组件。</param><param name="render">首次绘制动作。</param><param name="readInput">宿主输入循环。</param>
    /// <param name="token">取消信号。</param><returns>完整选择值。</returns>
    private static async Task<string?> RunSelectionAsync(CodingAgentAuthSelectorComponent selector, Action render,
        Func<CancellationToken, Task> readInput, CancellationToken token)
    {
        string? selected = null;
        var cancelled = false;
        Action<TuiSelectItem> onSelected = item => selected = item.Value;
        Action onCancelled = () => cancelled = true;
        selector.Selected += onSelected;
        selector.Cancelled += onCancelled;
        try
        {
            token.ThrowIfCancellationRequested();
            render();
            while (selected is null && !cancelled) await readInput(token).ConfigureAwait(false);
            return cancelled ? null : selected;
        }
        finally
        {
            selector.Selected -= onSelected;
            selector.Cancelled -= onCancelled;
            selector.Focused = false;
        }
    }

    private static string FormatDescription(ProviderAuthStatus status)
    {
        var configured = status.IsConfigured ? "configured" : "missing";
        var login = status.CanLogin ? ", login available" : string.Empty;
        var oauth = status.UsesOAuth ? ", oauth" : string.Empty;
        return $"{configured} via {status.Source}{oauth}{login}";
    }
}
