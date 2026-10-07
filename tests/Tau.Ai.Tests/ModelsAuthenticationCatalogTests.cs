// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【认证目录测试】认证入口不依赖提供方是否已有模型快照。</summary>
public sealed class ModelsAuthenticationCatalogTests
{
    /// <summary>【AI】【认证目录测试】全部内置名称都能查询认证定义，Meta 支持环境密钥及订阅登录。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task BuiltIns_ExposeAuthenticationWithoutModelSnapshot()
    {
        var configuration = new ModelConfigurationStore([]);
        var resolver = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: configuration);
        var models = BuiltInProviders.CreateBuiltInModels(configuration, resolver);
        foreach (var id in BuiltInProviderNames.All.Keys) Assert.NotNull(models.GetProvider(id)?.Auth);
        var meta = models.GetProvider("meta")!;
        Assert.Equal("Meta Model API key", meta.Auth!.ApiKey!.Name);
        Assert.Equal("Sign in with Meta", meta.Auth.OAuth!.LoginLabel);
        var auth = await meta.Auth.ApiKey.ResolveAsync(new("meta", null, new Dictionary<string, string> { ["META_API_KEY"] = "test-key" }, default));
        Assert.Equal("test-key", auth!.ApiKey);
    }

    /// <summary>【AI】【认证目录测试】仅认证定义合法，全空定义仍报错。</summary>
    [Fact]
    public void AuthenticationOnlyProvider_IsValid()
    {
        var auth = new ProviderAuthDefinition(apiKey: new ApiKeyAuthDefinition("Key", _ => Task.FromResult<ProviderAuthResult?>(null)));
        var provider = new ProviderDefinition("auth-only", auth: auth);
        Assert.Empty(provider.GetModels());
        Assert.Same(auth, provider.Auth);
        Assert.Throws<ArgumentException>(() => new ProviderDefinition("empty"));
    }
}
