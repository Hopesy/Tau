// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed class RuntimeProviderAuthenticationTests
{
    /// <summary>【AI】【认证归属】API key-only 原生定义屏蔽内置 OAuth，发布后调用方修改字典不影响快照，注销恢复内置定义。</summary>
    [Fact]
    public void NativeAuthentication_OwnsMethodsUntilUnregistered()
    {
        var resolver = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: new ModelConfigurationStore([]));
        var baseline = resolver.GetOAuthProvider("anthropic");
        var keys = new Dictionary<string, ApiKeyAuthDefinition> { ["anthropic"] = new("Native Key", _ => Task.FromResult<ProviderAuthResult?>(null)) };
        var owners = new HashSet<string> { "anthropic" };
        resolver.SetRuntimeAuthenticationProviders(new Dictionary<string, IOAuthProvider>(), keys, owners);
        keys.Clear(); owners.Clear();
        Assert.Null(resolver.GetOAuthProvider("anthropic"));
        Assert.NotNull(resolver.GetApiKeyProvider("anthropic"));
        Assert.True(resolver.HasNativeAuthentication("anthropic"));
        resolver.SetRuntimeAuthenticationProviders(new Dictionary<string, IOAuthProvider>(), new Dictionary<string, ApiKeyAuthDefinition>(), []);
        Assert.Same(baseline, resolver.GetOAuthProvider("anthropic"));
        Assert.Null(resolver.GetApiKeyProvider("anthropic"));
    }

    /// <summary>【AI】【认证类型隔离】OAuth-only 原生定义不接受遗留 API key 或环境密钥，显式请求覆盖仍按已有契约生效。</summary>
    [Fact]
    public void NativeOAuthOnly_DoesNotUseLegacyStoredOrEnvironmentKey()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"anthropic":{"type":"api_key","key":"legacy-fixture"}}""");
            var resolver = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([path]), configurationStore: new ModelConfigurationStore([]));
            var oauth = resolver.GetOAuthProvider("anthropic")!;
            resolver.SetRuntimeAuthenticationProviders(new Dictionary<string, IOAuthProvider> { ["anthropic"] = oauth }, new Dictionary<string, ApiKeyAuthDefinition>(), ["anthropic"]);
            var environment = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "environment-fixture" };
            Assert.False(resolver.GetStatus("anthropic", env: environment).IsConfigured);
            Assert.Null(resolver.ResolveRequestAuth("anthropic", env: environment).ApiKey);
            Assert.Equal("explicit-fixture", resolver.ResolveRequestAuth("anthropic", "explicit-fixture", environment).ApiKey);
        }
        finally { File.Delete(path); }
    }

    /// <summary>【AI】【认证目录】不依赖模型注册即可列出认证方式，兼容注册不屏蔽内置 OAuth。</summary>
    [Fact]
    public void AuthenticationProviders_IncludeEmptyModelProvidersAndLegacyFallback()
    {
        var resolver = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: new ModelConfigurationStore([]));
        resolver.SetRuntimeApiKeyProviders(new Dictionary<string, ApiKeyAuthDefinition> { ["empty-models"] = new("Pending", _ => Task.FromResult<ProviderAuthResult?>(null)) });
        Assert.Contains("empty-models", resolver.GetAuthenticationProviderIds());
        Assert.NotNull(resolver.GetOAuthProvider("anthropic"));
        Assert.False(resolver.HasNativeAuthentication("anthropic"));
    }
}
