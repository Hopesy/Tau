// 作者：xxx
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentAuthSelectorComponentTests
{
    /// <summary>【CodingAgent】【认证键盘】独立终端和组合终端都能实际输入包含 j/k 的查询并选中认证方式。</summary>
    /// <param name="composition">是否使用组合终端。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selector_SearchInputReachesActualSession(bool composition)
    {
        var state = State() with { InitialFilter = "no-result" };
        var keys = new AuthKeys(new('\u0015', ConsoleKey.U, false, false, true),
            new('j', ConsoleKey.J, false, false, false), new('k', ConsoleKey.K, false, false, false),
            new('e', ConsoleKey.E, false, false, false), new('y', ConsoleKey.Y, false, false, false), Key(ConsoleKey.Enter));
        var surface = new AuthSurface(72, 24);
        string? selected;
        if (composition)
        {
            var session = new TuiCompositionSession(surface, keys);
            selected = await CodingAgentAuthSelector.CreateCompositionSelector(session)(state, default);
            Assert.False(session.HasVisibleOverlay);
            Assert.False(session.HasFocusedInputOverlay);
        }
        else selected = await CodingAgentAuthSelector.SelectAsync(state, keys, surface);
        Assert.Equal(state.Options[1].SelectionKey, selected);
        Assert.Contains(surface.Diffs.SelectMany(diff => diff.Operations), operation => operation.Text.Contains("No matching providers", StringComparison.Ordinal));
        Assert.Contains(surface.Diffs.SelectMany(diff => diff.Operations), operation => operation.Text.Contains("Select provider to configure:", StringComparison.Ordinal));
    }

    /// <summary>【CodingAgent】【认证取消】空列表仍显示可取消界面，Escape 和 Ctrl+C 都关闭覆盖层。</summary>
    /// <param name="controlC">是否使用 Ctrl+C。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selector_EmptyLogoutCanBeCancelled(bool controlC)
    {
        var surface = new AuthSurface(80, 24);
        var keys = new AuthKeys(controlC ? new('\u0003', ConsoleKey.C, false, false, true) : Key(ConsoleKey.Escape));
        var session = new TuiCompositionSession(surface, keys);
        var selected = await CodingAgentAuthSelector.CreateCompositionSelector(session)(new("", []) { Mode = "logout" }, default);
        Assert.Null(selected);
        Assert.False(session.HasVisibleOverlay);
        Assert.Contains(surface.Diffs.SelectMany(diff => diff.Operations), operation => operation.Text.Contains("No providers logged in. Use /login first.", StringComparison.Ordinal));
    }

    /// <summary>【CodingAgent】【认证导航】方向键在列表边界停止，滚动窗口最多显示八个提供方。</summary>
    [Fact]
    public void Selector_ClampsNavigationAndRendersEightRows()
    {
        var component = new CodingAgentAuthSelectorComponent(new("", []) { Mode = "login",
            Options = Enumerable.Range(0, 12).Select(index => new CodingAgentAuthOption("p" + index, "Provider " + index, "api_key")).ToArray() });
        for (var i = 0; i < 15; i++) component.HandleInput(Key(ConsoleKey.DownArrow));
        Assert.Equal("Provider 11", component.SelectedItem!.Label);
        var lines = component.Render(80);
        Assert.Equal(8, lines.Count(line => line.Contains("Provider ", StringComparison.Ordinal)));
        Assert.Contains(lines, line => line.Contains("(12/12)", StringComparison.Ordinal));
        for (var i = 0; i < 15; i++) component.HandleInput(Key(ConsoleKey.UpArrow));
        Assert.Equal("Provider 0", component.SelectedItem!.Label);
    }

    /// <summary>【CodingAgent】【搜索编辑】光标移动和删除保留完整 emoji 字素，窄终端渲染不越界。</summary>
    [Fact]
    public void Selector_EditsUnicodeGraphemesAndFitsNarrowWidths()
    {
        var component = new CodingAgentAuthSelectorComponent(State());
        component.SetSearch("甲👩‍🔬乙");
        component.HandleInput(Key(ConsoleKey.LeftArrow));
        component.HandleInput(Key(ConsoleKey.Backspace));
        Assert.Equal("甲乙", component.Search);
        Assert.Equal(1, component.CursorPosition);
        component.HandleInput(new('k', ConsoleKey.K, false, false, false));
        Assert.Equal("甲k乙", component.Search);
        component.HandleInput(Key(ConsoleKey.Home));
        component.HandleInput(Key(ConsoleKey.Delete));
        Assert.Equal("k乙", component.Search);
        component.SetSearch("long query 甲👩‍🔬乙");
        foreach (var width in new[] { 1, 2, 3, 4, 8, 12, 40 })
            Assert.All(component.Render(width), line => Assert.InRange(TuiText.VisibleWidth(line), 0, width));
    }

    /// <summary>【CodingAgent】【认证颜色】当前主题用于选择、已配置状态和警告，失去焦点时隐藏输入光标。</summary>
    [Fact]
    public void Selector_UsesThemeAndFocusState()
    {
        var state = State() with { Options = [new("a", "Alpha", "api_key", new("a", true, "stored credential", false, true, ""))] };
        var theme = new CodingAgentTheme("fixture", null, "test", new Dictionary<string, string>
            { ["accent"] = "#123456", ["success"] = "42", ["border"] = "8" }, new Dictionary<string, string>());
        var component = new CodingAgentAuthSelectorComponent(state, theme: theme);
        var output = string.Join("\n", component.Render(90));
        Assert.Contains("\u001b[38;2;18;52;86m", output);
        Assert.Contains("\u001b[38;5;42m ✓ configured", output);
        Assert.Contains("\u001b[7m", output);
        component.Focused = false;
        Assert.DoesNotContain("\u001b[7m", string.Join("\n", component.Render(90)));
    }

    /// <summary>【CodingAgent】【认证异常】输入取消抛出异常时组合终端也释放覆盖层。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Selector_CancellationCleansUpCompositionOverlay()
    {
        using var cancellation = new CancellationTokenSource();
        var keys = new CancellingAuthKeys(cancellation);
        var session = new TuiCompositionSession(new AuthSurface(80, 24), keys);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodingAgentAuthSelector.CreateCompositionSelector(session)(State(), cancellation.Token));
        Assert.False(session.HasVisibleOverlay);
    }

    /// <summary>创建包含明确搜索目标的认证状态。</summary><returns>测试认证列表。</returns>
    private static CodingAgentAuthSelectorState State() => new("", []) { Mode = "login", Options =
        [new("first", "Alpha", "oauth", MethodName: "Account"), new("jkey", "JKey Provider", "api_key", MethodName: "Key Login")] };
    /// <summary>创建控制键。</summary><param name="key">键值。</param><returns>按键事件。</returns>
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    /// <summary>按顺序提供脚本输入。</summary><param name="keys">输入事件。</param>
    private sealed class AuthKeys(params ConsoleKeyInfo[] keys) : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);
        /// <summary>读取下一按键。</summary><param name="cancellationToken">取消信号。</param><returns>脚本按键。</returns>
        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_keys.Dequeue()); }
    }
    /// <summary>在读取时取消会话。</summary><param name="source">取消源。</param>
    private sealed class CancellingAuthKeys(CancellationTokenSource source) : IConsoleKeyReader
    {
        /// <summary>触发取消并拒绝输入。</summary><param name="cancellationToken">会话信号。</param><returns>取消任务。</returns>
        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        { source.Cancel(); return ValueTask.FromCanceled<ConsoleKeyInfo>(cancellationToken); }
    }
    /// <summary>保存终端实际绘制操作。</summary><param name="width">列数。</param><param name="height">行数。</param>
    private sealed class AuthSurface(int width, int height) : ITuiRenderSurface
    {
        public int Width => width;
        public int Height => height;
        public List<TuiRenderDiff> Diffs { get; } = [];
        /// <summary>记录宿主发出的帧差异。</summary><param name="diff">渲染操作。</param>
        public void Apply(TuiRenderDiff diff) => Diffs.Add(diff);
    }
}
