// 作者：xxx
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentAuthSelectorComponentTests
{
    /// <summary>【CodingAgent】【登录导航】先选方式，提供方列表返回后可切换到另一方式。</summary>
    /// <param name="composition">是否使用组合终端。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthFlow_BackFromProvidersReturnsToMethodMenu(bool composition)
    {
        var state = State() with { SelectMethodFirst = true };
        var surface = new AuthSurface(80, 24);
        var keys = new AuthKeys(Key(ConsoleKey.Enter), Key(ConsoleKey.Escape),
            Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter), Key(ConsoleKey.Enter));
        var selected = composition
            ? await CodingAgentAuthSelector.CreateCompositionSelector(new TuiCompositionSession(surface, keys))(state, default)
            : await CodingAgentAuthSelector.SelectAsync(state, keys, surface);
        Assert.Equal(state.Options[1].SelectionKey, selected);
        Assert.Contains(surface.Diffs.SelectMany(diff => diff.Operations), operation => operation.Text.Contains("Select authentication method:", StringComparison.Ordinal));
        Assert.Contains(surface.Diffs.SelectMany(diff => diff.Operations), operation => operation.Text.Contains("Sign in with an API key", StringComparison.Ordinal));
    }

    /// <summary>【CodingAgent】【指定登录】同一提供方直接选择认证方式并显示其自定义账户登录标签。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AuthFlow_SingleProviderUsesCustomLoginLabel()
    {
        var state = State() with { SelectMethodFirst = true, Options =
            [new("dual", "Dual Provider", "oauth") { LoginLabel = "Use custom account" }, new("dual", "Dual Provider", "api_key")] };
        var surface = new AuthSurface(90, 24);
        var selected = await CodingAgentAuthSelector.SelectAsync(state, new AuthKeys(Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter)), surface);
        Assert.Equal(state.Options[1].SelectionKey, selected);
        var output = string.Join("\n", surface.Diffs.SelectMany(diff => diff.Operations).Select(operation => operation.Text));
        Assert.Contains("Select authentication method for Dual Provider:", output);
        Assert.Contains("Use custom account", output);
        Assert.DoesNotContain("not configured", output);
    }

    /// <summary>【CodingAgent】【Radius 登录】顶层第三个快捷选项直接选择 Radius OAuth。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AuthFlow_RadiusShortcutSkipsProviderList()
    {
        var state = State() with { SelectMethodFirst = true, Options =
            [.. State().Options, new("radius", "Radius", "oauth", new("radius", true, "stored credential", true, true, ""))] };
        var surface = new AuthSurface(90, 24);
        var selected = await CodingAgentAuthSelector.SelectAsync(state,
            new AuthKeys(Key(ConsoleKey.DownArrow), Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter)), surface);
        Assert.Equal(state.Options[2].SelectionKey, selected);
        Assert.Contains(surface.Diffs.SelectMany(diff => diff.Operations), operation => operation.Text.Contains("Sign in with Radius ✓ configured", StringComparison.Ordinal));
    }

    /// <summary>【CodingAgent】【登录导航取消】关闭顶层方式菜单会结束选择，不进入提供方列表。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AuthFlow_TopLevelCancellationEndsSelection()
    {
        var state = State() with { SelectMethodFirst = true };
        Assert.Null(await CodingAgentAuthSelector.SelectAsync(state, new AuthKeys(Key(ConsoleKey.Escape)), new AuthSurface(80, 24)));
    }
}

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【登录引用】显示名包含空格时能定位提供方，不完整名称预填搜索框。</summary>
    /// <param name="command">实际命令。</param><param name="filter">预期初始搜索词。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("/login Dual Provider", null)]
    [InlineData("/login \"Dual Provider\"", null)]
    [InlineData("/login dual p", "dual p")]
    public async Task AuthFlow_LoginAcceptsDisplayNamesAndPartialQueries(string command, string? filter)
    {
        using var fixture = new Fixture("auth-name-login", "models");
        WriteDualAuthExtension(fixture);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        var router = new CodingAgentCommandRouter(session.Runner, extensionCommandStore: session.ExtensionCommandStore, authSelector: (state, _) =>
        {
            Assert.Equal(filter, state.InitialFilter);
            Assert.Equal(filter is null, state.SelectMethodFirst);
            var option = Assert.Single(state.Options, option => option.Provider == "dual" && option.AuthType == "api_key");
            if (filter is null) Assert.Equal(2, state.Options.Count);
            return Task.FromResult<string?>(option.SelectionKey);
        });
        var result = await router.TryHandleAsync(command);
        Assert.False(result!.IsError, result.Message);
        Assert.Contains("authenticated successfully", result.Message);
    }

    /// <summary>【CodingAgent】【登录返回】单次秘密提示取消后回到选择器，不保存返回前的任何输入。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AuthFlow_CancelledPromptReturnsToSelector()
    {
        using var fixture = new Fixture("auth-prompt-back", "models");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        var callbacks = new BuiltInKeyLoginCallbacks { AfterPrompt = () => throw new OperationCanceledException("Prompt cancelled") };
        var selections = 0;
        var router = new CodingAgentCommandRouter(session.Runner, oauthLoginCallbacksFactory: () => callbacks, authSelector: (state, _) =>
        {
            selections++;
            return Task.FromResult<string?>(selections == 1 ? state.Options.Single(option => option.Provider == "deepseek" && option.AuthType == "api_key").SelectionKey : null);
        });
        var result = await router.TryHandleAsync("/login");
        Assert.Equal("login selection cancelled", result!.Message);
        Assert.Equal(2, selections);
        Assert.Single(callbacks.SecretPrompts);
        Assert.DoesNotContain(session.Runner.ListStoredCredentials(), entry => entry.ProviderId == "deepseek");
    }
}
