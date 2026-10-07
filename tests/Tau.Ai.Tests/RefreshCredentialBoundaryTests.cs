// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【目录刷新认证测试】区分原始缓存恢复、目录联网发现及聊天请求的认证窗口。</summary>
public sealed class RefreshCredentialBoundaryTests
{
    /// <summary>【AI】【目录刷新窗口】目录发现只刷新已过期令牌，聊天请求仍提前五分钟刷新。</summary>
    /// <param name="minutes">旧凭据剩余分钟数。</param><param name="catalog">是否目录发现。</param><param name="refresh">预期是否刷新。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(2, true, false)]
    [InlineData(2, false, true)]
    [InlineData(-1, true, true)]
    [InlineData(60, true, false)]
    public async Task RefreshWindow_DependsOnOperation(int minutes, bool catalog, bool refresh)
    {
        using var fixture = new Fixture(); var provider = new RefreshProvider();
        fixture.Store.Save(provider.Id, Credential(minutes));
        fixture.Resolver.SetRuntimeOAuthProviders(new Dictionary<string, IOAuthProvider> { [provider.Id] = provider });
        var access = catalog ? (await fixture.Resolver.ResolveRefreshCredentialAsync(provider.Id, true))!.Value.GetProperty("access").GetString()
            : fixture.Resolver.ResolveRequestAuth(provider.Id).ApiKey;
        Assert.Equal(refresh ? "renewed" : "stored", access); Assert.Equal(refresh ? 1 : 0, provider.Calls);
    }

    /// <summary>【AI】【刷新凭据归属】联网发现不接受缺少处理器的保存凭据；离线缓存仍获得原始值。</summary>
    /// <param name="oauth">是否保存 OAuth。</param><param name="network">是否联网发现。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnsupportedCredentials_AreOnlyAvailableOffline(bool oauth, bool network)
    {
        using var fixture = new Fixture();
        if (oauth) fixture.Store.Save("refresh-test", Credential());
        else fixture.Store.SaveApiKey("refresh-test", new("stored-key"));
        fixture.Resolver.SetRuntimeAuthenticationProviders(new Dictionary<string, IOAuthProvider>(), new Dictionary<string, ApiKeyAuthDefinition>(), ["refresh-test"]);
        var result = await fixture.Resolver.ResolveRefreshCredentialAsync("refresh-test", network);
        if (network) Assert.Null(result);
        else Assert.Equal(oauth ? "oauth" : "api_key", result!.Value.GetProperty("type").GetString());
    }

    /// <summary>【AI】【离线恢复边界】原生离线阶段不解析配置，旧扩展兼容入口可显式保留此行为。</summary>
    /// <param name="legacy">是否旧扩展兼容入口。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Offline_OnlyLegacyAdapterResolvesConfiguredKey(bool legacy)
    {
        using var fixture = new Fixture();
        var result = await fixture.Resolver.ResolveRefreshCredentialAsync("refresh-test", false, resolveLegacyOfflineConfiguration: legacy);
        if (legacy) Assert.Equal("configured-key", result!.Value.GetProperty("key").GetString());
        else Assert.Null(result);
        Assert.Equal("configured-key", (await fixture.Resolver.ResolveRefreshCredentialAsync("refresh-test", true))!.Value.GetProperty("key").GetString());
    }

    /// <summary>【AI】【认证取消边界】联网发现及同步请求都可取消不响应信号的原生密钥委托。</summary>
    /// <param name="catalog">是否目录发现。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeKeyResolution_CanCancelNonCooperativeTask(bool catalog)
    {
        using var fixture = new Fixture(); using var source = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderAuthResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Resolver.SetRuntimeApiKeyProviders(new Dictionary<string, ApiKeyAuthDefinition> { ["refresh-test"] = new("Key", _ => { entered.TrySetResult(); return release.Task; }) });
        Task pending = catalog ? fixture.Resolver.ResolveRefreshCredentialAsync("refresh-test", true, source.Token)
            : Task.Run(() => fixture.Resolver.ResolveRequestAuth("refresh-test", token: source.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); source.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { release.TrySetResult(new("late-key")); }
    }

    /// <summary>【AI】【认证取消边界】进入前已取消时，即使有显式密钥或可用凭据也应报告取消。</summary>
    [Fact]
    public void PreCancelledRequest_DoesNotReturnExplicitKey()
    {
        using var fixture = new Fixture();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Resolver.ResolveRequestAuth("refresh-test", "explicit", token: new(true)));
    }

    /// <summary>【AI】【刷新测试凭据】构造指定剩余期限的合成凭据。</summary><param name="minutes">剩余分钟数。</param><returns>凭据。</returns>
    private static OAuthCredentials Credential(int minutes = 60) => new() { Access = "stored", Refresh = "refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(minutes) };

    /// <summary>【AI】【刷新测试配置】使用唯一临时目录并显式隔离内置环境。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tau-refresh-boundary-" + Guid.NewGuid().ToString("N"));
        public OAuthCredentialStore Store { get; }
        public ProviderAuthResolver Resolver { get; }
        /// <summary>创建模型配置、凭据文件及解析器。</summary>
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var config = Path.Combine(_root, "models.json");
            File.WriteAllText(config, """{"providers":{"refresh-test":{"apiKey":"configured-key"}}}""");
            Store = new([Path.Combine(_root, "auth.json")]);
            Resolver = new(new OAuthProviderRegistry([]), Store, configurationStore: new ModelConfigurationStore([config]));
        }
        /// <summary>释放本测试专属目录。</summary>
        public void Dispose() => Directory.Delete(_root, true);
    }

    /// <summary>【AI】【刷新测试提供方】统计刷新次数并返回合成的新令牌。</summary>
    private sealed class RefreshProvider : IOAuthProvider
    {
        public string Id => "refresh-test";
        public string Name => "Refresh";
        public int Calls { get; private set; }
        /// <summary>测试不执行登录。</summary><param name="callbacks">交互。</param><param name="cancellationToken">信号。</param><returns>未使用。</returns>
        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>生成新令牌并增加计数。</summary><param name="credentials">旧凭据。</param><param name="cancellationToken">信号。</param><returns>新凭据。</returns>
        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(Credential() with { Access = "renewed" }); }
        /// <summary>返回合成访问令牌。</summary><param name="credentials">凭据。</param><returns>令牌。</returns>
        public string GetApiKey(OAuthCredentials credentials) => credentials.Access;
    }
}
