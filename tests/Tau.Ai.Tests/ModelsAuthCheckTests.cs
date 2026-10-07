// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Providers;

namespace Tau.Ai.Tests;

/// <summary>【AI】【认证检查测试】验证凭据归属、取消、并发可用目录及原始凭据过滤。</summary>
public sealed class ModelsAuthCheckTests
{
    /// <summary>【AI】【认证归属】保存的类型没有处理器时不能回退到另一种认证。</summary><param name="oauth">是否保存 OAuth。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsupportedStoredType_DoesNotFallBack(bool oauth)
    {
        var credentials = new Store { Value = oauth ? OAuthCredential() : new ProviderCredential.ApiKey(new("stored")) };
        var auth = oauth ? new ProviderAuthDefinition(apiKey: new("Key", _ => throw new InvalidOperationException("Unexpected resolve"),
            check: _ => throw new InvalidOperationException("Unexpected check"))) : new ProviderAuthDefinition(oauth: OAuth());
        var models = new Models([Definition("test", auth)], credentialStore: credentials);
        Assert.Null(await models.CheckAuthAsync("test")); Assert.Empty(await models.GetAllAvailableAsync());
    }

    /// <summary>【AI】【OAuth 状态】过期 OAuth 仍表示已配置，状态查询不会刷新或派生令牌。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task OAuthStatus_DoesNotRefreshExpiredCredential()
    {
        var models = new Models([Definition("test", new(oauth: OAuth()))], credentialStore: new Store { Value = OAuthCredential() });
        var status = await models.CheckAuthAsync("test");
        Assert.True(status!.IsConfigured); Assert.True(status.UsesOAuth); Assert.Equal("OAuth", status.Source);
        Assert.Single(await models.GetAvailableAsync());
    }

    /// <summary>【AI】【显式密钥】仅 OAuth 的提供方忽略请求密钥，仍按原始 OAuth 凭据判断状态。</summary><param name="saved">是否保存 OAuth。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitKey_RequiresApiKeyHandler(bool saved)
    {
        var models = new Models([Definition("test", new(oauth: OAuth()))], credentialStore: new Store { Value = saved ? OAuthCredential() : null });
        Assert.Equal(saved, (await models.CheckAuthAsync("test", apiKey: "explicit"))?.IsConfigured == true);
    }

    /// <summary>【AI】【显式密钥校验】包含空字符串的显式密钥交给提供方，不可直接报告成功或读取旧凭据。</summary>
    /// <param name="check">是否提供专用检查。</param><param name="key">显式密钥。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(true, "explicit")]
    [InlineData(false, "explicit")]
    [InlineData(true, "")]
    [InlineData(false, "")]
    public async Task ExplicitKey_IsValidatedByProvider(bool check, string key)
    {
        var calls = 0;
        var auth = new ProviderAuthDefinition(apiKey: new("Key", context =>
        { Assert.False(check); Assert.Equal(key, context.Credential!.Key); calls++; return Task.FromResult<ProviderAuthResult?>(null); },
            check: check ? context => { Assert.Equal(key, context.Credential!.Key); calls++; return Task.FromResult<ProviderAuthStatus?>(Status("test", false)); } : null));
        var models = new Models([Definition("test", auth)], credentialStore: new Store { Read = () => throw new InvalidOperationException("Do not read stored credentials") });
        Assert.False((await models.CheckAuthAsync("test", apiKey: key))!.IsConfigured); Assert.Equal(1, calls);
    }

    /// <summary>【AI】【检查环境】请求环境覆盖保存字段，未覆盖字段仍可用于专用检查。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Check_MergesCredentialEnvironment()
    {
        var credentials = new Store { Value = new ProviderCredential.ApiKey(new("key", new Dictionary<string, string> { ["shared"] = "stored", ["saved"] = "yes" })) };
        var auth = new ProviderAuthDefinition(apiKey: new("Key", _ => throw new InvalidOperationException(), check: context =>
        {
            Assert.Equal("request", context.Credential!.Env!["shared"]); Assert.Equal("yes", context.Credential.Env["saved"]);
            return Task.FromResult<ProviderAuthStatus?>(Status("test"));
        }));
        Assert.True((await new Models([Definition("test", auth)], credentialStore: credentials).CheckAuthAsync("test", env: new Dictionary<string, string> { ["shared"] = "request" }))!.IsConfigured);
    }

    /// <summary>【AI】【预取消】未知提供方和空集合也必须响应已取消的请求。</summary><param name="available">是否查询目录。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCanceledRequest_ThrowsEvenWithoutProviders(bool available)
    {
        using var source = new CancellationTokenSource(); source.Cancel(); var models = new Models();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { if (available) await models.GetAllAvailableAsync(cancellationToken: source.Token); else await models.CheckAuthAsync("missing", cancellationToken: source.Token); });
    }

    /// <summary>【AI】【非协作取消】存储、检查或解析忽略信号时，调用方仍可及时取消且不进入后续过滤。</summary>
    /// <param name="stage">暂停阶段。</param><param name="available">是否查询目录。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("read", false)]
    [InlineData("check", false)]
    [InlineData("resolve", false)]
    [InlineData("read", true)]
    [InlineData("check", true)]
    [InlineData("resolve", true)]
    public async Task Cancellation_InterruptsNonCooperativeWork(string stage, bool available)
    {
        using var source = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var filtered = false;
        var store = new Store();
        if (stage == "read") store.Read = async () => { entered.TrySetResult(); await release.Task; return null; };
        var auth = new ProviderAuthDefinition(apiKey: new("Key", async _ =>
        { entered.TrySetResult(); await release.Task; return new("late"); }, check: stage == "check" ? async _ =>
        { entered.TrySetResult(); await release.Task; return Status("test"); } : null));
        var provider = new ProviderDefinition("test", models: [Model("test")], auth: auth, filterAllModels: (models, _) => { filtered = true; return models; });
        var models = new Models([provider], credentialStore: store);
        Task pending = available ? models.GetAllAvailableAsync(cancellationToken: source.Token) : models.CheckAuthAsync("test", cancellationToken: source.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); source.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(filtered);
        }
        finally { release.TrySetResult(); }
    }

    /// <summary>【AI】【错误分类】未取消请求中的存储、检查和解析失败均保留认证错误类别。</summary><param name="stage">失败阶段。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("read")]
    [InlineData("check")]
    [InlineData("resolve")]
    public async Task Failures_AreAuthenticationErrors(string stage)
    {
        var store = new Store(); if (stage == "read") store.Read = () => throw new IOException("Fixture read error");
        var auth = new ProviderAuthDefinition(apiKey: new("Key", _ => throw new IOException("Fixture resolve error"),
            check: stage == "check" ? _ => throw new OperationCanceledException("Independent provider failure") : null));
        var models = new Models([Definition("test", auth)], credentialStore: store);
        Assert.Equal("auth", (await Assert.ThrowsAsync<ModelsError>(() => models.CheckAuthAsync("test"))).Code);
        Assert.Equal("auth", (await Assert.ThrowsAsync<ModelsError>(() => models.GetAllAvailableAsync())).Code);
    }

    /// <summary>【AI】【并行检查】慢提供方不阻止其他检查启动，完成后仍按注册顺序返回模型。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Availability_ChecksConcurrentlyAndPreservesRegistrationOrder()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new Models([Definition("first", Checked(async _ => { first.TrySetResult(); await release.Task; return Status("first"); })),
            Definition("second", Checked(_ => { second.TrySetResult(); return Task.FromResult<ProviderAuthStatus?>(Status("second")); }))]);
        var pending = models.GetAllAvailableAsync();
        try { await Task.WhenAll(first.Task, second.Task).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        Assert.Equal(new[] { "first", "second" }, (await pending).Select(model => model.Provider));
    }

    /// <summary>【AI】【过滤快照】认证检查期间替换凭据，目录过滤仍接收本轮最初读取的快照。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Availability_FilterReceivesOriginalCredential()
    {
        var original = new ProviderCredential.ApiKey(new("original")); var store = new Store { Value = original };
        var provider = new ProviderDefinition("test", models: [Model("test")], auth: Checked(_ =>
        { store.Value = new ProviderCredential.ApiKey(new("replacement")); return Task.FromResult<ProviderAuthStatus?>(Status("test")); }),
            filterAllModels: (models, credential) => { Assert.Same(original, credential); return models; });
        Assert.Single(await new Models([provider], credentialStore: store).GetAllAvailableAsync()); Assert.Equal(1, store.Reads);
    }

    /// <summary>【AI】【并行失败】一个检查失败时立即返回错误，不等待另一提供方的不协作检查。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Availability_FailsWithoutWaitingForOtherProviders()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new Models([Definition("slow", Checked(async _ => { await release.Task; return Status("slow"); })),
            Definition("broken", Checked(_ => throw new IOException("Fixture failure")))]);
        try { Assert.Equal("auth", (await Assert.ThrowsAsync<ModelsError>(() => models.GetAllAvailableAsync().WaitAsync(TimeSpan.FromSeconds(5)))).Code); }
        finally { release.TrySetResult(); }
    }

    /// <summary>【AI】【测试认证】构造带专用检查的密钥定义。</summary><param name="check">检查函数。</param><returns>认证定义。</returns>
    private static ProviderAuthDefinition Checked(Func<ProviderAuthCheckContext, Task<ProviderAuthStatus?>> check) => new(apiKey: new("Key", _ => throw new InvalidOperationException("Check must take precedence"), check));
    /// <summary>【AI】【测试 OAuth】所有网络或派生操作均禁止。</summary><returns>认证定义。</returns>
    private static OAuthAuthDefinition OAuth() => new("OAuth", _ => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
    /// <summary>【AI】【测试凭据】返回过期合成 OAuth。</summary><returns>凭据。</returns>
    private static ProviderCredential OAuthCredential() => new ProviderCredential.OAuth(new("refresh", "access", DateTimeOffset.UnixEpoch));
    /// <summary>【AI】【测试状态】返回指定提供方的密钥状态。</summary><param name="id">标识。</param><param name="configured">是否配置。</param><returns>状态。</returns>
    private static ProviderAuthStatus Status(string id, bool configured = true) => new(id, configured, "fixture", false, true, "Fixture status");
    /// <summary>【AI】【测试定义】创建单模型提供方。</summary><param name="id">标识。</param><param name="auth">认证。</param><returns>提供方。</returns>
    private static ProviderDefinition Definition(string id, ProviderAuthDefinition auth) => new(id, models: [Model(id)], auth: auth);
    /// <summary>【AI】【测试模型】创建无网络的模型。</summary><param name="provider">提供方。</param><returns>模型。</returns>
    private static Model Model(string provider) => new() { Id = "chat", Name = "Chat", Provider = provider, Api = "fixture", BaseUrl = "https://fixture.invalid", ContextWindow = 1000, MaxOutputTokens = 100 };

    /// <summary>【AI】【测试存储】支持受控读取和替换，用于验证取消及快照。</summary>
    private sealed class Store : IProviderCredentialStore
    {
        public ProviderCredential? Value { get; set; }
        public Func<Task<ProviderCredential?>>? Read { get; set; }
        public int Reads { get; private set; }
        /// <inheritdoc />
        public Task<ProviderCredential?> ReadAsync(string providerId, CancellationToken cancellationToken = default) { Reads++; return Read?.Invoke() ?? Task.FromResult(Value); }
        /// <inheritdoc />
        public Task<IReadOnlyList<ProviderCredentialInfo>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProviderCredentialInfo>>([]);
        /// <inheritdoc />
        public Task<ProviderCredential?> ModifyAsync(string providerId, Func<ProviderCredential?, Task<ProviderCredential?>> mutation, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Status must not refresh OAuth");
        /// <inheritdoc />
        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
