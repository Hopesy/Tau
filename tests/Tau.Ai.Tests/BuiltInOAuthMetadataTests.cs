// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed class BuiltInOAuthMetadataTests
{
    /// <summary>【AI】【OAuth 元数据】公开 Models 与旧 OAuth 注册共享认证名称、订阅标记和登录菜单标签。</summary>
    /// <param name="provider">提供方。</param><param name="name">认证名称。</param><param name="label">可选登录文案。</param>
    [Theory]
    [InlineData("anthropic", "Anthropic (Claude Pro/Max)", null)]
    [InlineData("github-copilot", "GitHub Copilot", null)]
    [InlineData("openai-codex", "OpenAI (ChatGPT Plus/Pro)", null)]
    [InlineData("xai", "xAI (Grok/X subscription)", "Sign in with SuperGrok or X Premium")]
    [InlineData("meta", "Meta (Muse subscription)", "Sign in with Meta")]
    [InlineData("kimi-coding", "Kimi Code (subscription)", "Sign in with Kimi Code")]
    [InlineData("openai", "OpenAI (ChatGPT subscription)", "Sign in with ChatGPT")]
    public void Models_PreserveBuiltInOAuthMetadata(string provider, string name, string? label)
    {
        var configuration = new ModelConfigurationStore([]);
        var resolver = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: configuration);
        var implementation = resolver.GetOAuthProvider(provider)!;
        Assert.Equal(name, implementation.Name);
        Assert.True(implementation.IsSubscription);
        Assert.Equal(label, implementation.LoginLabel);
        var definition = BuiltInProviders.CreateBuiltInModels(configuration, resolver).GetProvider(provider)!.Auth!;
        Assert.Equal(name, definition.OAuth!.Name);
        Assert.True(definition.OAuth.IsSubscription);
        Assert.Equal(label, definition.OAuth.LoginLabel);
        if (provider == "openai-codex") Assert.Null(definition.ApiKey);
        if (provider == "github-copilot") Assert.Equal("GitHub Copilot token", definition.ApiKey!.Name);
    }
}
