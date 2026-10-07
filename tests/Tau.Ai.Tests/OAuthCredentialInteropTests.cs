// 作者：xxx
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【凭据互通测试】验证原生期限、结构化字段与 Models SDK 认证派生无损往返。</summary>
public sealed class OAuthCredentialInteropTests
{
    /// <summary>【AI】【凭据互通测试】JavaScript 安全整数期限不会导致 .NET 日期越界，也不会丢失原值。</summary>
    /// <param name="milliseconds">原始毫秒值。</param><param name="expired">是否过期。</param>
    [Theory]
    [InlineData(9007199254740991L, false)]
    [InlineData(-9007199254740991L, true)]
    [InlineData(253402300799999L, false)]
    [InlineData(-62135596800000L, true)]
    [InlineData(0L, true)]
    public void NativeExpiry_RoundTripsThroughJsonSdkAndStore(long milliseconds, bool expired)
    {
        using var json = JsonDocument.Parse($$$$"""{"type":"oauth","access":"test-key","refresh":"","expires":{{{{milliseconds}}}},"account":{"id":42},"scopes":["chat"],"enabled":true,"env":{"REGION":"test"}}""");
        var original = OAuthCredentialJson.Read(json.RootElement);
        var credential = ProviderOAuthCredential.FromOAuth(original).ToOAuth();
        Assert.Equal(expired, credential.IsExpired());
        Assert.Equal(milliseconds, OAuthCredentialJson.Write(credential).GetProperty("expires").GetInt64());
        using var fixture = new CredentialFile();
        File.WriteAllText(fixture.Path, "{\"provider\":" + json.RootElement.GetRawText() + "}");
        var store = new OAuthCredentialStore([fixture.Path]);
        var imported = store.Load()["provider"];
        Assert.Equal(milliseconds, imported.ExpiresUnixTimeMilliseconds);
        store.Save("provider", imported);
        var loaded = store.Load()["provider"];
        var native = OAuthCredentialJson.Write(loaded);
        Assert.Equal(milliseconds, native.GetProperty("expires").GetInt64());
        Assert.Equal(42, native.GetProperty("account").GetProperty("id").GetInt32());
        Assert.Equal("chat", native.GetProperty("scopes")[0].GetString());
        Assert.True(native.GetProperty("enabled").GetBoolean());
        Assert.Equal("test", native.GetProperty("env").GetProperty("REGION").GetString());
        using var saved = JsonDocument.Parse(File.ReadAllText(fixture.Path));
        Assert.Equal(milliseconds, saved.RootElement.GetProperty("provider").GetProperty("expires").GetInt64());
    }

    /// <summary>【AI】【凭据互通测试】刷新仅修改日期时，不沿用已失效的原生期限。</summary>
    [Fact]
    public void DateUpdate_ReplacesPreviousNativeExpiry()
    {
        using var json = JsonDocument.Parse("""{"access":"key","refresh":"","expires":9007199254740991}""");
        var expiry = DateTimeOffset.FromUnixTimeMilliseconds(1234567890);
        var updated = OAuthCredentialJson.Read(json.RootElement) with { ExpiresAt = expiry };
        Assert.Equal(1234567890, OAuthCredentialJson.Write(updated).GetProperty("expires").GetInt64());
        Assert.True(updated.IsExpired());
    }

    /// <summary>【AI】【凭据互通测试】保存后仍兼容旧日期字段，并保留 .NET 精细时间。</summary>
    [Fact]
    public void LegacyExpiry_PreservesDatePrecision()
    {
        using var fixture = new CredentialFile();
        var expiry = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(12345);
        var store = new OAuthCredentialStore([fixture.Path]);
        store.Save("legacy", new OAuthCredentials { Access = "key", Refresh = "refresh", ExpiresAt = expiry });
        Assert.Equal(expiry, store.Load()["legacy"].ExpiresAt);
        using var json = JsonDocument.Parse(File.ReadAllText(fixture.Path));
        Assert.Equal(expiry.ToUnixTimeMilliseconds(), json.RootElement.GetProperty("legacy").GetProperty("expires").GetInt64());
        Assert.Equal(expiry.ToString("O"), json.RootElement.GetProperty("legacy").GetProperty("expiresAt").GetString());
    }

    /// <summary>【AI】【凭据互通测试】原生期限与旧日期冲突时，以原生字段为准。</summary>
    [Fact]
    public void ConflictingExpiry_PrefersNativeValue()
    {
        using var fixture = new CredentialFile();
        File.WriteAllText(fixture.Path, """{"provider":{"access":"key","refresh":"","expires":9007199254740991,"expiresAt":"2020-01-01T00:00:00Z"}}""");
        var credential = new OAuthCredentialStore([fixture.Path]).Load()["provider"];
        Assert.False(credential.IsExpired());
        Assert.Equal(9007199254740991L, credential.ExpiresUnixTimeMilliseconds);
    }

    /// <summary>【AI】【凭据互通测试】SDK 登录、刷新与认证派生保留 JSON 字段、期限、请求头、地址和环境。</summary>
    /// <param name="id">普通提供方或覆盖旧特殊处理名称。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("openrouter")]
    [InlineData("anthropic")]
    public async Task ModelsAuth_PreservesCompleteProviderContract(string id)
    {
        using var json = JsonDocument.Parse("""{"access":"first","refresh":"","expires":9007199254740991,"account":{"id":42},"scopes":["chat"],"enabled":true}""");
        var provider = new StructuredProvider(id, OAuthCredentialJson.Read(json.RootElement));
        var configuration = new ModelConfigurationStore([]);
        var resolver = new ProviderAuthResolver(new OAuthProviderRegistry([provider]), new OAuthCredentialStore([]), configurationStore: configuration);
        var auth = BuiltInProviders.CreateBuiltInModels(configuration, resolver).GetProvider(id)!.Auth!.OAuth!;
        var login = await auth.LoginAsync(new NoInteraction());
        Assert.Equal(42, login.Properties["account"].GetProperty("id").GetInt32());
        Assert.Equal(9007199254740991L, login.ExpiresUnixTimeMilliseconds);
        var refreshed = await auth.RefreshAsync(login, default);
        Assert.Equal("next", refreshed.Access);
        var resolved = await auth.ToAuthAsync(refreshed);
        Assert.Null(resolved.ApiKey);
        Assert.Equal("Bearer next", resolved.Headers!["Authorization"]);
        Assert.Equal("https://account.invalid/v1", resolved.BaseUrl);
        Assert.Equal("test", resolved.Env!["REGION"]);
        Assert.Equal("structured OAuth", resolved.Source);
    }

    /// <summary>【AI】【凭据互通测试】SDK Copilot 认证保留企业端点或令牌内端点，不提前替换访问令牌。</summary>
    /// <param name="access">访问令牌。</param><param name="domain">企业域。</param><param name="expected">预期端点。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("token", "enterprise.test", "https://copilot-api.enterprise.test")]
    [InlineData("tid=test;proxy-ep=proxy.region.example", null, "https://api.region.example")]
    public async Task CopilotSdk_UsesCredentialEndpoint(string access, string? domain, string expected)
    {
        var configuration = new ModelConfigurationStore([]);
        var resolver = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: configuration);
        var auth = BuiltInProviders.CreateBuiltInModels(configuration, resolver).GetProvider("github-copilot")!.Auth!.OAuth!;
        var result = await auth.ToAuthAsync(new ProviderOAuthCredential("", access, DateTimeOffset.MaxValue,
            domain is null ? null : new Dictionary<string, string> { ["enterpriseUrl"] = domain }));
        Assert.Equal(access, result.ApiKey);
        Assert.Equal(expected, result.BaseUrl);
    }

    /// <summary>【AI】【凭据互通测试】注册表元数据包括订阅类型和自定义登录标签。</summary>
    [Fact]
    public void ProviderInfo_PreservesAccountMetadata()
    {
        var providers = new OAuthProviderRegistry().GetProviderInfoList();
        Assert.True(Assert.Single(providers, item => item.Id == "xai").IsSubscription);
        Assert.Equal("Sign in with OpenRouter", Assert.Single(providers, item => item.Id == "openrouter").LoginLabel);
    }

    /// <summary>【AI】【凭据互通测试】用完整认证派生替代仅提取 API key。</summary>
    private sealed class StructuredProvider(string id, OAuthCredentials initial) : IOAuthProvider
    {
        public string Id => id;
        public string Name => "Structured OAuth";
        /// <summary>返回初始结构化凭据。</summary><param name="callbacks">交互。</param><param name="cancellationToken">信号。</param><returns>凭据。</returns>
        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) => Task.FromResult(initial);
        /// <summary>验证输入字段并旋转访问令牌。</summary><param name="credentials">凭据。</param><param name="cancellationToken">信号。</param><returns>刷新凭据。</returns>
        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
        { Assert.Equal(42, credentials.Properties["account"].GetProperty("id").GetInt32()); Assert.Equal(9007199254740991L, credentials.ExpiresUnixTimeMilliseconds); return Task.FromResult(credentials with { Access = "next" }); }
        /// <summary>完整认证派生不应退回旧接口。</summary><param name="credentials">凭据。</param><returns>未使用。</returns>
        public string GetApiKey(OAuthCredentials credentials) => throw new InvalidOperationException("Full auth must be used");
        /// <summary>返回仅请求头认证及自定义端点。</summary><param name="credentials">凭据。</param><param name="token">信号。</param><returns>完整认证。</returns>
        public ProviderAuthResult ResolveAuth(OAuthCredentials credentials, CancellationToken token = default)
        {
            Assert.True(credentials.Properties["enabled"].GetBoolean());
            return new(Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + credentials.Access }, BaseUrl: "https://account.invalid/v1",
                Env: new Dictionary<string, string> { ["REGION"] = "test" }, Source: "structured OAuth");
        }
    }

    /// <summary>【AI】【凭据互通测试】此模拟认证无需用户输入。</summary>
    private sealed class NoInteraction : AuthInteraction
    {
        public CancellationToken CancellationToken => default;
        /// <summary>禁止意外提示。</summary><param name="prompt">提示。</param><returns>未使用。</returns>
        public Task<string> PromptAsync(string prompt) => throw new NotSupportedException();
        /// <summary>禁止意外通知。</summary><param name="message">消息。</param>
        public void Notify(string message) => throw new NotSupportedException();
    }

    /// <summary>【AI】【凭据互通测试】隔离测试凭据文件。</summary>
    private sealed class CredentialFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-oauth-interop-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "auth.json");
        /// <summary>创建唯一临时目录。</summary>
        public CredentialFile() => Directory.CreateDirectory(_directory);
        /// <summary>删除本测试唯一目录。</summary>
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
