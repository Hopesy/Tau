// 作者：xxx
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【登录选择器测试】验证默认方式、导航、主题及取消后的输入恢复。</summary>
public sealed partial class CodingAgentOAuthPromptSelectorTests
{
    private static readonly OAuthSelectOption[] Options = [new("browser", "Browser login", "Local callback"), new("device", "Device login", "Headless")];

    /// <summary>【CodingAgent】【方式导航】方向键停留在边界，j/k 与方向键一致。</summary>
    [Fact]
    public void Component_NavigationClampsAtBoundaries()
    {
        var component = new CodingAgentOAuthPromptSelector.Component("Select method", Options);
        component.HandleInput(Key(ConsoleKey.UpArrow)); Assert.Equal(0, component.SelectedIndex);
        component.HandleInput(new('j', ConsoleKey.J, false, false, false)); Assert.Equal(1, component.SelectedIndex);
        component.HandleInput(Key(ConsoleKey.DownArrow)); Assert.Equal(1, component.SelectedIndex);
        component.HandleInput(new('k', ConsoleKey.K, false, false, false)); Assert.Equal(0, component.SelectedIndex);
        component.HandleInput(Key(ConsoleKey.Enter)); Assert.Equal("browser", component.Selection);
    }

    /// <summary>【CodingAgent】【默认选择】普通控制台与组合终端均支持回车首项和方向选择。</summary>
    /// <param name="composition">是否组合终端。</param><param name="move">是否向下选择。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Selection_UsesDefaultAndKeyboard(bool composition, bool move)
    {
        var keys = new Keys(move ? [Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter)] : [Key(ConsoleKey.Enter)]);
        var surface = new Surface(); var session = new TuiCompositionSession(surface, keys);
        var result = composition
            ? await CodingAgentOAuthPromptSelector.CreateCompositionSelector(session)("Select method", Options, default)
            : await CodingAgentOAuthPromptSelector.SelectAsync("Select method", Options, keys, surface, default);
        Assert.Equal(move ? "device" : "browser", result); Assert.False(session.HasVisibleOverlay); Assert.False(session.HasFocusedInputOverlay);
        Assert.True(surface.Frames > 0);
    }

    /// <summary>【CodingAgent】【取消恢复】Escape 或外部取消都清理组合覆盖层。</summary>
    /// <param name="external">是否外部取消。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompositionCancellation_RemovesOverlay(bool external)
    {
        using var source = new CancellationTokenSource();
        IConsoleKeyReader keys = external ? new CancelKeys(source) : new Keys([Key(ConsoleKey.Escape)]);
        var session = new TuiCompositionSession(new Surface(), keys);
        var select = CodingAgentOAuthPromptSelector.CreateCompositionSelector(session);
        if (external) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => select("Select", Options, source.Token));
        else Assert.Null(await select("Select", Options, source.Token));
        Assert.False(session.HasVisibleOverlay); Assert.False(session.HasFocusedInputOverlay);
    }

    /// <summary>【CodingAgent】【交互接线】类型化选择完整传递标题、ID、说明与取消信号，不进入文本编辑器。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task InteractiveCallbacks_UseInjectedSelector()
    {
        var terminal = new FakeTerminal(); using var source = new CancellationTokenSource();
        var callbacks = new InteractiveOAuthLoginCallbacks(new InteractiveConsoleSession(terminal), (message, options, token) =>
        { Assert.Equal("Choose", message); Assert.Same(Options, options); Assert.Equal(source.Token, token); return Task.FromResult<string?>(options[1].Id); });
        Assert.Equal("device", await callbacks.OnSelectAsync("Choose", Options, source.Token)); Assert.Empty(terminal.Writes);
    }

    /// <summary>【CodingAgent】【取消语义】选择器关闭映射为登录取消，不作为未知提供方方式提交。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task InteractiveCallbacks_TranslateCancelledSelection()
    {
        var callbacks = new InteractiveOAuthLoginCallbacks(new InteractiveConsoleSession(new FakeTerminal()), (_, _, _) => Task.FromResult<string?>(null));
        Assert.Equal("Login cancelled", (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callbacks.OnSelectAsync("Choose", Options, default))).Message);
    }

    /// <summary>【CodingAgent】【文本回退】没有图形选择器时回车仍选择首项。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task TextFallback_EnterChoosesFirstOption()
    {
        var terminal = new FakeTerminal(); terminal.QueueInput("");
        Assert.Equal("browser", await new InteractiveOAuthLoginCallbacks(new InteractiveConsoleSession(terminal)).OnSelectAsync("Choose", Options, default));
    }

    /// <summary>【CodingAgent】【窄终端主题】标题与说明按可见宽度换行，并使用当前主题。</summary>
    [Fact]
    public void Component_RenderPreservesDescriptionsWithinWidth()
    {
        var theme = new CodingAgentTheme("test", null, "fixture", new Dictionary<string, string> { ["accent"] = "#123456", ["border"] = "8" }, new Dictionary<string, string>());
        var component = new CodingAgentOAuthPromptSelector.Component("Choose 登录方式", Options, theme);
        var lines = component.Render(12);
        Assert.All(lines, line => Assert.True(TuiText.VisibleWidth(line) <= 12));
        Assert.Contains("\u001b[38;2;18;52;86m", string.Join("\n", lines));
        Assert.Contains("Headless", string.Join("\n", lines));
    }

    /// <summary>【CodingAgent】【设备显示】类型化设备通知只显示链接和代码，保留终端状态输出。</summary>
    [Fact]
    public void InteractiveDeviceCode_DisplaysCodeAndAddress()
    {
        var terminal = new FakeTerminal(); var callbacks = new InteractiveOAuthLoginCallbacks(new InteractiveConsoleSession(terminal));
        callbacks.OnDeviceCode(new("USER", "https://unit.invalid/device", 1.5, 900));
        Assert.Contains("Enter code: USER", terminal.FlattenedText()); Assert.Contains("https://unit.invalid/device", terminal.FlattenedText());
    }

    /// <summary>【CodingAgent】【按键夹具】创建无修饰控制键。</summary><param name="key">键名。</param><returns>事件。</returns>
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    /// <summary>【CodingAgent】【输入夹具】按脚本读取。</summary><param name="keys">输入序列。</param>
    private sealed class Keys(IEnumerable<ConsoleKeyInfo> keys) : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);
        /// <inheritdoc />
        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_keys.Dequeue()); }
    }

    /// <summary>【CodingAgent】【取消夹具】首次读取时取消。</summary><param name="source">取消源。</param>
    private sealed class CancelKeys(CancellationTokenSource source) : IConsoleKeyReader
    {
        /// <inheritdoc />
        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default) { source.Cancel(); return ValueTask.FromCanceled<ConsoleKeyInfo>(cancellationToken); }
    }

    /// <summary>【CodingAgent】【渲染夹具】记录渲染次数，保持正常终端视口。</summary>
    private sealed class Surface : ITuiRenderSurface
    {
        public int Width => 80;
        public int Height => 24;
        public int Frames { get; private set; }
        /// <inheritdoc />
        public void Apply(TuiRenderDiff diff) => Frames++;
    }
}
