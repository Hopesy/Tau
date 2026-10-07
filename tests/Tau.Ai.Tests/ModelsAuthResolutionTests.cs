// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Providers;

namespace Tau.Ai.Tests;

/// <summary>【AI】【认证解析测试】覆盖凭据归属、串行刷新、取消及有效期契约。</summary>
public sealed class ModelsAuthResolutionTests
{
    private static readonly Model Model = new() { Provider = "auth-test", Id = "chat", Name = "Chat", Api = "openai-responses" };

    /// <summary>【AI】【凭据归属测试】不支持的已保存凭据不能退回其他认证方式。</summary>
    /// <param name="oauthStored">是否保存 OAuth 而只提供 API key 处理器。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedStoredType_DoesNotFallBack(bool oauthStored)
    {
        var store = new InMemoryProviderCredentialStore();
        await store.ModifyAsync(Model.Provider, _ => Task.FromResult<ProviderCredential?>(oauthStored
            ? new ProviderCredential.OAuth(Credential()) : new ProviderCredential.ApiKey(new("stored-key"))));
        var auth = oauthStored ? new ProviderAuthDefinition(apiKey: new("Key", _ => throw new InvalidOperationException("Ambient fallback")))
            : new ProviderAuthDefinition(oauth: OAuth());
        Assert.Null(await Create(auth, store).ResolveAuthAsync(Model));
    }

    /// <summary>【AI】【凭据竞态测试】互斥区内已退出、改为密钥或已刷新时，以最新存储为准。</summary>
    /// <param name="replacement">锁内的新凭据类型。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("deleted")]
    [InlineData("api_key")]
    [InlineData("oauth")]
    public async Task Refresh_RechecksCredentialUnderLock(string replacement)
    {
        var store = new RacingStore(new ProviderCredential.OAuth(Credential(-1)), replacement switch
        {
            "oauth" => new ProviderCredential.OAuth(Credential() with { Access = "other-request" }),
            "api_key" => new ProviderCredential.ApiKey(new("replacement-key")),
            _ => null
        });
        var auth = new ProviderAuthDefinition(oauth: OAuth(refresh: (_, _) => throw new InvalidOperationException("Must not refresh stale credential")));
        var result = await Create(auth, store).ResolveAuthAsync(Model);
        Assert.Equal(replacement == "oauth" ? "other-request" : null, result?.ApiKey);
    }

    /// <summary>【AI】【并发刷新测试】并行请求共享刷新结果，只执行一次令牌轮换。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ConcurrentResolution_RefreshesOnce()
    {
        var store = new InMemoryProviderCredentialStore();
        await Save(store, Credential(-1));
        var refreshResult = new TaskCompletionSource<ProviderOAuthCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var models = Create(new(oauth: OAuth(refresh: (_, _) => { Interlocked.Increment(ref calls); return refreshResult.Task; })), store);
        var requests = Enumerable.Range(0, 12).Select(_ => models.ResolveAuthAsync(Model)).ToArray();
        refreshResult.SetResult(Credential() with { Access = "rotated" });
        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal("rotated", result!.ApiKey)); Assert.Equal(1, calls);
    }

    /// <summary>【AI】【认证取消测试】非协作的密钥解析、OAuth 刷新、派生与存储读取都能取消调用方等待。</summary>
    /// <param name="stage">暂停阶段。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("api_key")]
    [InlineData("refresh")]
    [InlineData("derive")]
    [InlineData("read")]
    public async Task Cancellation_InterruptsNonCooperativeStage(string stage)
    {
        using var source = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RacingStore(new ProviderCredential.OAuth(Credential(stage == "refresh" ? -1 : 60)), new ProviderCredential.OAuth(Credential(-1)));
        if (stage == "read") store.ReadOverride = async () => { entered.TrySetResult(); await release.Task; return null; };
        var auth = stage == "api_key"
            ? new ProviderAuthDefinition(apiKey: new("Key", async _ => { entered.TrySetResult(); await release.Task; return new("late"); }))
            : new ProviderAuthDefinition(oauth: OAuth(
                refresh: async (_, _) => { entered.TrySetResult(); await release.Task; return Credential(); },
                derive: async current => { if (stage == "derive") { entered.TrySetResult(); await release.Task; } return new(current.Access); }));
        var models = Create(auth, store);
        var pending = models.ResolveAuthAsync(Model, apiKey: stage == "api_key" ? "explicit" : null, cancellationToken: source.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); source.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { release.TrySetResult(); }
    }

    /// <summary>【AI】【刷新时限测试】独立超时和调用方取消都保留旧凭据，迟到刷新不能提交。</summary>
    /// <param name="cancel">是否由调用方取消，否则使用独立超时。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrTimedOutRefresh_DoesNotCommitLateResult(bool cancel)
    {
        using var source = new CancellationTokenSource();
        var store = new InMemoryProviderCredentialStore(); await Save(store, Credential(-1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ProviderOAuthCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken refreshToken = default;
        var auth = new ProviderAuthDefinition(oauth: OAuth(refresh: (_, token) => { refreshToken = token; entered.TrySetResult(); return late.Task; }));
        var models = new Models([new ProviderDefinition(Model.Provider, models: [Model], auth: auth)], credentialStore: store)
            { OAuthRefreshTimeout = cancel ? TimeSpan.FromSeconds(15) : TimeSpan.FromMilliseconds(50) };
        var pending = models.ResolveAuthAsync(Model, cancellationToken: source.Token);
        await entered.Task;
        if (cancel)
        {
            source.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            var error = await Assert.ThrowsAsync<ModelsError>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("oauth", error.Code);
        }
        Assert.True(refreshToken.IsCancellationRequested);
        late.SetResult(Credential() with { Access = "late" });
        await store.ModifyAsync(Model.Provider, _ => Task.FromResult<ProviderCredential?>(null));
        Assert.Equal("stored", Assert.IsType<ProviderCredential.OAuth>(await store.ReadAsync(Model.Provider)).Value.Access);
    }

    /// <summary>【AI】【认证错误测试】刷新与派生错误标记为 OAuth 错误，API key 错误保留 auth 分类。</summary>
    /// <param name="stage">失败阶段。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("refresh")]
    [InlineData("derive")]
    [InlineData("api_key")]
    public async Task Errors_KeepAuthenticationStage(string stage)
    {
        var store = new InMemoryProviderCredentialStore(); await Save(store, Credential(stage == "refresh" ? -1 : 60));
        var cause = new InvalidOperationException("synthetic failure");
        var auth = stage == "api_key" ? new ProviderAuthDefinition(apiKey: new("Key", _ => throw cause))
            : new ProviderAuthDefinition(oauth: OAuth(refresh: (_, _) => throw cause, derive: _ => throw cause));
        var error = await Assert.ThrowsAsync<ModelsError>(() => Create(auth, store).ResolveAuthAsync(Model, apiKey: stage == "api_key" ? "explicit" : null));
        Assert.Equal(stage == "api_key" ? "auth" : "oauth", error.Code); Assert.Same(cause, error.InnerException);
    }

    /// <summary>【AI】【有效期测试】显式最低有效期扩大刷新窗口，刷新后不足时拒绝派生认证。</summary>
    /// <param name="requestedMinutes">请求的最低分钟数；空时使用普通五分钟窗口。</param><param name="nextMinutes">刷新后剩余分钟数。</param>
    /// <param name="expectError">是否要求拒绝结果。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(null, 1, false)]
    [InlineData(0, 1, true)]
    [InlineData(30, 10, true)]
    [InlineData(30, 60, false)]
    public async Task MinimumValidity_IsCheckedAfterExplicitRefresh(int? requestedMinutes, int nextMinutes, bool expectError)
    {
        var store = new InMemoryProviderCredentialStore(); await Save(store, Credential(requestedMinutes == 30 ? 10 : -1));
        var calls = 0;
        var models = Create(new(oauth: OAuth(refresh: (_, _) => { calls++; return Task.FromResult(Credential(nextMinutes) with { Access = "fresh" }); })), store);
        var pending = models.ResolveAuthAsync(Model, minimumOAuthValidity: requestedMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null);
        if (expectError) Assert.Equal("oauth", (await Assert.ThrowsAsync<ModelsError>(() => pending)).Code);
        else Assert.Equal("fresh", (await pending)!.ApiKey);
        Assert.Equal(1, calls);
    }

    /// <summary>【AI】【认证环境测试】请求环境覆盖合并进已保存的 API key 凭据，并保留未覆盖字段。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ApiKeyCredential_ReceivesMergedEnvironment()
    {
        var store = new InMemoryProviderCredentialStore();
        await store.ModifyAsync(Model.Provider, _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.ApiKey(new("stored", new Dictionary<string, string> { ["REGION"] = "old", ["PROFILE"] = "saved" }))));
        var auth = new ProviderAuthDefinition(apiKey: new("Key", context =>
        {
            Assert.Equal("request", context.Credential!.Env!["REGION"]); Assert.Equal("saved", context.Credential.Env["PROFILE"]);
            return Task.FromResult<ProviderAuthResult?>(new(context.Credential.Key));
        }));
        Assert.Equal("stored", (await Create(auth, store).ResolveAuthAsync(Model, env: new Dictionary<string, string> { ["REGION"] = "request" }))!.ApiKey);
    }

    /// <summary>【AI】【凭据删除测试】删除排在正在执行的修改后，刷新不会复活已退出的认证。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Delete_IsSerializedWithMutation()
    {
        var store = new InMemoryProviderCredentialStore(); await Save(store, Credential());
        var release = new TaskCompletionSource<ProviderCredential?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = store.ModifyAsync(Model.Provider, _ => release.Task);
        var deletion = store.DeleteAsync(Model.Provider);
        Assert.False(deletion.IsCompleted);
        release.SetResult(new ProviderCredential.OAuth(Credential() with { Access = "rotated" }));
        await Task.WhenAll(mutation, deletion);
        Assert.Null(await store.ReadAsync(Model.Provider));
    }

    /// <summary>【AI】【凭据取消测试】非协作修改取消后释放互斥锁，迟到结果不能写入。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task StoreCancellation_ReleasesLockWithoutCommitting()
    {
        using var source = new CancellationTokenSource();
        var store = new InMemoryProviderCredentialStore(); await Save(store, Credential());
        var release = new TaskCompletionSource<ProviderCredential?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = store.ModifyAsync(Model.Provider, _ => release.Task, source.Token);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation.WaitAsync(TimeSpan.FromSeconds(5)));
        await store.DeleteAsync(Model.Provider).WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult(new ProviderCredential.OAuth(Credential()));
        Assert.Null(await store.ReadAsync(Model.Provider));
    }

    /// <summary>【AI】【认证测试工厂】构造只含合成令牌的凭据。</summary><param name="minutes">剩余分钟数。</param><returns>凭据。</returns>
    private static ProviderOAuthCredential Credential(int minutes = 60) => new("refresh", "stored", DateTimeOffset.UtcNow.AddMinutes(minutes));

    /// <summary>【AI】【认证测试工厂】建立可替换刷新和派生步骤的认证定义。</summary>
    /// <param name="refresh">刷新委托。</param><param name="derive">派生委托。</param><returns>认证定义。</returns>
    private static OAuthAuthDefinition OAuth(Func<ProviderOAuthCredential, CancellationToken, Task<ProviderOAuthCredential>>? refresh = null,
        Func<ProviderOAuthCredential, Task<ProviderAuthResult>>? derive = null) => new("OAuth", _ => Task.FromResult(Credential()),
            refresh ?? ((current, _) => Task.FromResult(current)), derive ?? (current => Task.FromResult(new ProviderAuthResult(current.Access))));

    /// <summary>【AI】【认证测试工厂】创建具有独立存储的 Models。</summary><param name="auth">认证定义。</param><param name="store">凭据存储。</param><returns>模型集合。</returns>
    private static Models Create(ProviderAuthDefinition auth, IProviderCredentialStore store) => new([new ProviderDefinition(Model.Provider, models: [Model], auth: auth)], credentialStore: store);

    /// <summary>【AI】【认证测试工厂】保存初始 OAuth 凭据。</summary><param name="store">存储。</param><param name="credential">凭据。</param><returns>存储完成任务。</returns>
    private static Task<ProviderCredential?> Save(IProviderCredentialStore store, ProviderOAuthCredential credential) => store.ModifyAsync(Model.Provider, _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.OAuth(credential)));

    /// <summary>【AI】【凭据竞态替身】第一次读返回旧快照，修改时返回已经变化的权威状态。</summary>
    /// <param name="snapshot">旧快照。</param><param name="current">修改时状态。</param>
    private sealed class RacingStore(ProviderCredential? snapshot, ProviderCredential? current) : IProviderCredentialStore
    {
        public Func<Task<ProviderCredential?>>? ReadOverride { get; set; }
        /// <summary>读取旧快照或受控异步结果。</summary><param name="providerId">标识。</param><param name="cancellationToken">信号。</param><returns>快照。</returns>
        public Task<ProviderCredential?> ReadAsync(string providerId, CancellationToken cancellationToken = default) => ReadOverride?.Invoke() ?? Task.FromResult(snapshot);
        /// <summary>不提供额外元数据。</summary><param name="cancellationToken">信号。</param><returns>空目录。</returns>
        public Task<IReadOnlyList<ProviderCredentialInfo>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProviderCredentialInfo>>([]);
        /// <summary>用权威状态执行修改，空结果保持原值。</summary><param name="providerId">标识。</param><param name="mutation">修改。</param><param name="cancellationToken">信号。</param><returns>最终状态。</returns>
        public async Task<ProviderCredential?> ModifyAsync(string providerId, Func<ProviderCredential?, Task<ProviderCredential?>> mutation, CancellationToken cancellationToken = default) => await mutation(current) ?? current;
        /// <summary>清除权威状态。</summary><param name="providerId">标识。</param><param name="cancellationToken">信号。</param><returns>完成任务。</returns>
        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default) { current = null; return Task.CompletedTask; }
    }
}
