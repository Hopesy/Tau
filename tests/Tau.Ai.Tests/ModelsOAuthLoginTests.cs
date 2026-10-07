// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Models OAuth 测试】验证公共登录入口、取消提交边界和原生期限刷新。</summary>
public sealed class ModelsOAuthLoginTests
{
    /// <summary>【AI】【Models OAuth 测试】统一和兼容登录入口都传递设备配置并保存到各自的凭据存储。</summary>
    /// <param name="legacy">是否兼容接口。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicLogin_PassesOptionsAndPersists(bool legacy)
    {
        using var fixture = new Fixture(); var provider = new LoginProvider();
        var (models, store) = fixture.Create(provider);
        var options = new OAuthLoginOptions(() => "installation-id");
        if (legacy)
        {
            Assert.Equal("installation-id", (await models.LoginAsync("openai", new Callbacks(), options: options)).Access);
            Assert.Equal("installation-id", new OAuthCredentialStore([fixture.AuthPath]).Load()["openai"].Access);
        }
        else
        {
            Assert.Equal("installation-id", Assert.IsType<ProviderCredential.OAuth>(await models.LoginAsync("openai", "oauth", new Interaction(), options)).Value.Access);
            Assert.Equal("installation-id", Assert.IsType<ProviderCredential.OAuth>(await store.ReadAsync("openai")).Value.Access);
        }
    }

    /// <summary>【AI】【Models OAuth 测试】提供方返回前发生取消时，旧凭据不被覆盖。</summary>
    /// <param name="legacy">是否兼容接口。</param><param name="preCancelled">是否进入登录前已经取消。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Cancellation_DoesNotCommitCredential(bool legacy, bool preCancelled)
    {
        using var fixture = new Fixture(); using var source = new CancellationTokenSource();
        var provider = new LoginProvider { BeforeReturn = source.Cancel };
        var (models, store) = fixture.Create(provider);
        var old = new OAuthCredentials { Access = "old", Refresh = "old-refresh", ExpiresAt = DateTimeOffset.MaxValue };
        new OAuthCredentialStore([fixture.AuthPath]).Save("openai", old);
        await store.ModifyAsync("openai", _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.OAuth(ProviderOAuthCredential.FromOAuth(old))));
        if (preCancelled) source.Cancel();
        var options = new OAuthLoginOptions(() => "new");
        if (legacy) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => models.LoginAsync("openai", new Callbacks(), source.Token, options));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => models.LoginAsync("openai", "oauth", new Interaction(source.Token), options));
        Assert.Equal(preCancelled ? 0 : 1, provider.Calls);
        Assert.Equal("old", new OAuthCredentialStore([fixture.AuthPath]).Load()["openai"].Access);
        Assert.Equal("old", Assert.IsType<ProviderCredential.OAuth>(await store.ReadAsync("openai")).Value.Access);
    }

    /// <summary>【AI】【Models OAuth 测试】極早期限会刷新，长期凭据不会因日期转换而刷新或溢出。</summary>
    /// <param name="expired">是否使用极早期限。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authentication_HandlesNativeExpiryBounds(bool expired)
    {
        var count = 0; var store = new InMemoryProviderCredentialStore();
        var credential = new ProviderOAuthCredential("refresh", "stored", expired ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue)
        { ExpiresUnixTimeMilliseconds = expired ? -9007199254740991L : 9007199254740991L };
        var definition = new OAuthAuthDefinition("Expiry", _ => Task.FromResult(credential), (current, _) =>
        { count++; return Task.FromResult(current with { Access = "renewed", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), ExpiresUnixTimeMilliseconds = null }); },
            current => Task.FromResult(new ProviderAuthResult(current.Access)));
        var model = new Model { Provider = "expiry", Id = "chat", Name = "Chat", Api = "openai-responses" };
        var models = new Models([new ProviderDefinition("expiry", models: [model], auth: new(oauth: definition))], credentialStore: store);
        await store.ModifyAsync("expiry", _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.OAuth(credential)));
        Assert.Equal(expired ? "renewed" : "stored", (await models.ResolveAuthAsync(model))!.ApiKey);
        Assert.Equal(expired ? 1 : 0, count);
    }

    /// <summary>【AI】【Models OAuth 测试】读取选项并在提交前执行取消。</summary>
    private sealed class LoginProvider : IOAuthProvider
    {
        public string Id => "openai";
        public string Name => "Login";
        public Action? BeforeReturn { get; init; }
        public int Calls { get; private set; }
        /// <summary>要求携带选项。</summary><param name="callbacks">交互。</param><param name="cancellationToken">信号。</param><returns>未使用。</returns>
        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>读取设备配置后返回合成凭据。</summary><param name="callbacks">交互。</param><param name="options">选项。</param><param name="cancellationToken">信号。</param><returns>合成凭据。</returns>
        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, OAuthLoginOptions? options, CancellationToken cancellationToken = default)
        {
            Calls++; var value = options!.GetDeviceId!(); BeforeReturn?.Invoke();
            return Task.FromResult(new OAuthCredentials { Access = value, Refresh = "refresh", ExpiresAt = DateTimeOffset.MaxValue });
        }
        /// <summary>返回原始凭据。</summary><param name="credentials">凭据。</param><param name="cancellationToken">信号。</param><returns>凭据。</returns>
        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) => Task.FromResult(credentials);
        /// <summary>读取访问令牌。</summary><param name="credentials">凭据。</param><returns>访问令牌。</returns>
        public string GetApiKey(OAuthCredentials credentials) => credentials.Access;
    }
    /// <summary>【AI】【Models OAuth 测试】无输入的统一交互。</summary><param name="token">取消信号。</param>
    private sealed class Interaction(CancellationToken token = default) : AuthInteraction
    {
        public CancellationToken CancellationToken => token;
        /// <summary>禁止意外输入。</summary><param name="prompt">提示。</param><returns>未使用。</returns>
        public Task<string> PromptAsync(string prompt) => throw new NotSupportedException();
        /// <summary>禁止意外通知。</summary><param name="message">消息。</param>
        public void Notify(string message) => throw new NotSupportedException();
    }
    /// <summary>【AI】【Models OAuth 测试】无输入的兼容交互。</summary>
    private sealed class Callbacks : IOAuthLoginCallbacks
    {
        /// <summary>禁止意外链接。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) => throw new NotSupportedException();
        /// <summary>禁止意外输入。</summary><param name="message">提示。</param><param name="placeholder">占位。</param><param name="allowEmpty">策略。</param><returns>未使用。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new NotSupportedException();
        /// <summary>禁止意外通知。</summary><param name="message">消息。</param>
        public void OnProgress(string message) => throw new NotSupportedException();
        /// <summary>无手动代码。</summary><returns>空。</returns>
        public Task<string>? OnManualCodeInputAsync() => null;
    }
    /// <summary>【AI】【Models OAuth 测试】隔离新旧凭据存储。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tau-models-login-" + Guid.NewGuid().ToString("N"));
        public string AuthPath => Path.Combine(_root, "auth.json");
        /// <summary>创建唯一测试目录。</summary>
        public Fixture() => Directory.CreateDirectory(_root);
        /// <summary>通过内置工厂获取真实认证桥接，再注入可观察的统一凭据存储。</summary><param name="provider">模拟认证。</param><returns>Models 及其存储。</returns>
        public (Models Models, InMemoryProviderCredentialStore Store) Create(IOAuthProvider provider)
        {
            var config = new ModelConfigurationStore([]);
            var resolver = new ProviderAuthResolver(new OAuthProviderRegistry([provider]), new OAuthCredentialStore([AuthPath]), configurationStore: config);
            var builtIn = BuiltInProviders.CreateBuiltInModels(config, resolver).GetProvider("openai")!;
            var store = new InMemoryProviderCredentialStore();
            return (new Models([builtIn], resolver, credentialStore: store), store);
        }
        /// <summary>释放测试目录。</summary>
        public void Dispose() => Directory.Delete(_root, true);
    }
}
