// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【OAuth 账户显示】Radius 与 OpenRouter 在真实会话提供账户和密钥方式，账户不标记为订阅。</summary>
    /// <param name="provider">提供方。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("radius")]
    [InlineData("openrouter")]
    public async Task AccountOAuthMetadata_ReachesLoginSelector(string provider)
    {
        using var fixture = new Fixture("account-oauth-metadata", "models");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        var router = new CodingAgentCommandRouter(session.Runner, authSelector: (state, _) =>
        {
            var option = Assert.Single(state.Options, item => item.Provider == provider && item.AuthType == "oauth");
            Assert.False(option.Subscription);
            Assert.Contains(state.Options, item => item.Provider == provider && item.AuthType == "api_key");
            Assert.True(state.SelectMethodFirst);
            if (provider == "openrouter") Assert.Equal("Sign in with OpenRouter", option.LoginLabel);
            return Task.FromResult<string?>(null);
        });
        Assert.Equal("login selection cancelled", (await router.TryHandleAsync("/login"))!.Message);
    }

    /// <summary>【CodingAgent】【OAuth 显示】真实会话选择器读取内置订阅属性和 xAI 登录文案，不启动外部登录。</summary>
    /// <param name="provider">提供方标识。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("anthropic")]
    [InlineData("github-copilot")]
    [InlineData("openai-codex")]
    [InlineData("xai")]
    [InlineData("meta")]
    [InlineData("kimi-coding")]
    [InlineData("openai")]
    public async Task OAuthMetadata_ReachesLoginSelector(string provider)
    {
        using var fixture = new Fixture("oauth-metadata", "models");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        var router = new CodingAgentCommandRouter(session.Runner, authSelector: (state, _) =>
        {
            var option = Assert.Single(state.Options, option => option.Provider == provider && option.AuthType == "oauth");
            Assert.True(option.Subscription);
            Assert.Equal("subscription", CodingAgentAuthSelector.FormatAuthType(option.AuthType, option.Subscription));
            if (provider == "xai") Assert.Equal("Sign in with SuperGrok or X Premium", option.LoginLabel);
            if (provider == "github-copilot") Assert.Contains(state.Options, option => option.Provider == provider && option.AuthType == "api_key");
            return Task.FromResult<string?>(null);
        });
        Assert.Equal("login selection cancelled", (await router.TryHandleAsync("/login"))!.Message);
    }
}
