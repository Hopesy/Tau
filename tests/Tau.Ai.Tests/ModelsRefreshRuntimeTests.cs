// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using CatalogStore = Tau.Ai.Registry.ModelsStore;

namespace Tau.Ai.Tests;

/// <summary>【AI】【SDK 动态目录测试】验证离线初始化、认证准备、持久化发布及刷新代次。</summary>
public sealed class ModelsRefreshRuntimeTests
{
    /// <summary>【AI】【刷新阶段测试】先恢复缓存再联网，联网阶段读取离线阶段发布后的缓存。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task NativeRefresh_RestoresCacheBeforeAuthenticatedNetworkPhase()
    {
        var store = new InMemoryModelsStore(); await store.WriteAsync("dynamic", new([Model("cached")]));
        var stages = new List<string>(); ProviderDefinition? provider = null;
        provider = new("dynamic", auth: KeyAuth(() => stages.Add("auth")), refreshWithContext: async context =>
        {
            stages.Add(context.AllowNetwork ? "online" : "offline");
            Assert.Equal(context.AllowNetwork ? true : null, context.Force);
            if (!context.AllowNetwork)
            {
                Assert.Null(context.Credential); Assert.Equal("cached", Assert.Single(context.Stored!.Models).Id);
                Assert.True(await context.PublishAsync(new() { Persist = new([Model("restored")]), Update = () => provider!.SetModels([Model("restored")]) }));
            }
            else
            {
                Assert.Equal("resolved-key", Assert.IsType<ProviderCredential.ApiKey>(context.Credential).Value.Key);
                Assert.Equal("restored", Assert.Single(context.Stored!.Models).Id);
                await context.PublishAsync(new() { Persist = new([Model("online")]), Update = () => provider!.SetModels([Model("online")]) });
            }
        });
        var models = new Models([provider], modelsStore: store);
        Assert.Empty((await models.RefreshAsync(force: true)).Errors);
        Assert.Equal(new[] { "offline", "auth", "online" }, stages);
        Assert.Equal("online", Assert.Single(models.GetModels()).Id);
        Assert.Equal("online", Assert.Single((await store.ReadAsync("dynamic"))!.Models).Id);
    }

    /// <summary>【AI】【离线测试】离线请求不解析认证；未配置认证的联网请求也仅运行缓存恢复。</summary>
    /// <param name="allowNetwork">是否请求联网。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineOrUnauthenticatedRefresh_RunsOnlyCachePhase(bool allowNetwork)
    {
        var calls = 0;
        var provider = new ProviderDefinition("dynamic", refreshWithContext: context =>
        { Assert.False(context.AllowNetwork); Assert.Null(context.Force); calls++; return Task.CompletedTask; });
        var models = new Models([provider]);
        Assert.Empty((await models.RefreshAsync(allowNetwork: allowNetwork, force: true)).Errors); Assert.Equal(1, calls);
    }

    /// <summary>【AI】【凭据错误测试】读取凭据失败仍恢复缓存，之后报告认证错误且不联网。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task CredentialReadFailure_StillRestoresCache()
    {
        var restored = false;
        var credentials = new BrokenCredentialStore();
        var models = new Models([new ProviderDefinition("dynamic", refreshWithContext: _ => { restored = true; return Task.CompletedTask; })], credentialStore: credentials);
        var result = await models.RefreshAsync();
        Assert.True(restored); Assert.Equal("auth", Assert.IsType<ModelsError>(result.Errors["dynamic"]).Code);
    }

    /// <summary>【AI】【目录 OAuth 测试】只刷新已过期令牌，联网阶段收到轮换后完整凭据。</summary>
    /// <param name="expired">是否过期。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogOAuth_RefreshesOnlyExpiredTokens(bool expired)
    {
        var store = new InMemoryProviderCredentialStore(); var refreshed = 0;
        var credential = new ProviderOAuthCredential("refresh", "stored", DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 2));
        await store.ModifyAsync("dynamic", _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.OAuth(credential)));
        var auth = new ProviderAuthDefinition(oauth: new("OAuth", _ => Task.FromResult(credential), (current, _) =>
        { refreshed++; return Task.FromResult(current with { Access = "renewed", ExpiresAt = DateTimeOffset.MaxValue }); }, _ => throw new InvalidOperationException("Do not derive request auth")));
        var phases = new List<string>();
        var models = new Models([new ProviderDefinition("dynamic", auth: auth, refreshWithContext: context =>
        { phases.Add(Assert.IsType<ProviderCredential.OAuth>(context.Credential).Value.Access); return Task.CompletedTask; })], credentialStore: store);
        Assert.Empty((await models.RefreshAsync()).Errors);
        Assert.Equal(new[] { "stored", expired ? "renewed" : "stored" }, phases); Assert.Equal(expired ? 1 : 0, refreshed);
    }

    /// <summary>【AI】【发布策略测试】未指定持久化保留缓存，显式空值删除缓存，两种情况都执行同步更新。</summary>
    /// <param name="delete">是否删除缓存。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_DistinguishesOmittedPersistenceAndDeletion(bool delete)
    {
        var store = new InMemoryModelsStore(); await store.WriteAsync("dynamic", new([Model("stored")])); var updated = false;
        var models = new Models([new ProviderDefinition("dynamic", refreshWithContext: async context =>
        {
            var publication = new ModelsPublication { Update = () => updated = true };
            if (delete) publication = publication with { Persist = null };
            Assert.True(await context.PublishAsync(publication));
        })], modelsStore: store);
        Assert.Empty((await models.RefreshAsync(allowNetwork: false)).Errors); Assert.True(updated);
        Assert.Equal(delete, await store.ReadAsync("dynamic") is null);
    }

    /// <summary>【AI】【发布失败测试】持久化失败不能运行内存更新，提供方错误会进入结果映射。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task PersistenceFailure_DoesNotRunUpdate()
    {
        var store = new ControlledStore { FailWrite = true }; var updated = false;
        var models = new Models([new ProviderDefinition("dynamic", refreshWithContext: async context =>
            await context.PublishAsync(new() { Persist = new([Model("new")]), Update = () => updated = true }))], modelsStore: store);
        var result = await models.RefreshAsync(allowNetwork: false);
        Assert.False(updated); Assert.IsType<IOException>(result.Errors["dynamic"]);
    }

    /// <summary>【AI】【快照隔离测试】即使存储直接返回共享对象，阶段上下文及发布入参也不会污染存储。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task StoredAndPublishedCatalogs_AreDeepCloned()
    {
        var original = Model("cached") with { Headers = new Dictionary<string, string> { ["X-Test"] = "original" } };
        var store = new ControlledStore { Entry = new([original, Model("future") with { Type = "future" }]) };
        var publication = new ModelsStoreEntry([Model("next") with { Headers = new Dictionary<string, string> { ["X-Test"] = "published" } }]);
        var models = new Models([new ProviderDefinition("dynamic", refreshWithContext: async context =>
        {
            Assert.Single(context.Stored!.Models); context.Stored.Models[0].Headers!["X-Test"] = "mutated";
            Assert.Equal("original", original.Headers!["X-Test"]);
            await context.PublishAsync(new() { Persist = publication, Update = () => publication.Models[0].Headers!["X-Test"] = "after" });
        })], modelsStore: store);
        Assert.Empty((await models.RefreshAsync(allowNetwork: false)).Errors);
        Assert.Equal("published", store.Entry!.Models[0].Headers!["X-Test"]);
    }

    /// <summary>【AI】【刷新代次测试】注册变化或新刷新取消旧工作，旧上下文不能发布目录。</summary>
    /// <param name="action">触发失效的操作。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("replace")]
    [InlineData("delete")]
    [InlineData("clear")]
    [InlineData("refresh")]
    public async Task SupersededRefresh_CannotPublish(string action)
    {
        var entered = new TaskCompletionSource<RefreshModelsContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0; var updated = false;
        var provider = new ProviderDefinition("dynamic", refreshWithContext: async context =>
        { if (++calls == 1) { entered.TrySetResult(context); await release.Task; } });
        var models = new Models([provider]); var first = models.RefreshAsync(allowNetwork: false);
        var old = await entered.Task;
        try
        {
            switch (action)
            {
                case "replace": models.SetProvider(new("dynamic", auth: KeyAuth())); break;
                case "delete": models.DeleteProvider("dynamic"); break;
                case "clear": models.ClearProviders(); break;
                default: Assert.Empty((await models.RefreshAsync(allowNetwork: false)).Errors); break;
            }
            var result = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Aborted); Assert.Empty(result.Errors); Assert.True(old.Signal.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.PublishAsync(new() { Update = () => updated = true }));
            Assert.False(updated);
        }
        finally { release.TrySetResult(); }
    }

    /// <summary>【AI】【调用方取消测试】不响应取消的提供方不能阻塞调用方结束，也不能迟到提交。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task CallerCancellation_ReturnsWithoutWaitingForProvider()
    {
        using var source = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new Models([new ProviderDefinition("dynamic", refreshWithContext: async _ => { entered.TrySetResult(); await release.Task; })]);
        var pending = models.RefreshAsync(cancellationToken: source.Token);
        await entered.Task; source.Cancel();
        try { var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(result.Aborted); Assert.Empty(result.Errors); }
        finally { release.TrySetResult(); }
    }

    /// <summary>【AI】【发布顺序测试】已取消的慢存储写入仍排在新发布之前，新目录最终获胜。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Publications_WaitForPreviousPersistenceEvenAfterCancellation()
    {
        var store = new ControlledStore { BlockFirstWrite = true }; var call = 0; var updates = new List<string>();
        var provider = new ProviderDefinition("dynamic", refreshWithContext: async context =>
        {
            var id = Interlocked.Increment(ref call) == 1 ? "old" : "new";
            await context.PublishAsync(new() { Persist = new([Model(id)]), Update = () => updates.Add(id) });
        });
        var models = new Models([provider], modelsStore: store); var old = models.RefreshAsync(allowNetwork: false);
        await store.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current = models.RefreshAsync(allowNetwork: false);
        await old.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(current.IsCompleted);
        store.ReleaseWrite.TrySetResult();
        Assert.Empty((await current.WaitAsync(TimeSpan.FromSeconds(5))).Errors);
        Assert.Equal(new[] { "old", "new" }, store.Writes); Assert.Equal(new[] { "new" }, updates);
        Assert.Equal("new", Assert.Single(store.Entry!.Models).Id);
    }

    /// <summary>【AI】【测试模型】构造最小聊天模型。</summary><param name="id">模型标识。</param><returns>模型。</returns>
    private static Model Model(string id) => new() { Provider = "dynamic", Id = id, Name = id, Api = "test" };

    /// <summary>【AI】【测试认证】构造可观察且不访问网络的密钥解析。</summary><param name="resolved">解析通知。</param><returns>认证定义。</returns>
    private static ProviderAuthDefinition KeyAuth(Action? resolved = null) => new(apiKey: new("Key", _ => { resolved?.Invoke(); return Task.FromResult<ProviderAuthResult?>(new("resolved-key")); }));

    /// <summary>【AI】【受控目录存储】故意不复制对象并允许暂停或拒绝写入，验证 SDK 的边界。</summary>
    private sealed class ControlledStore : CatalogStore
    {
        public ModelsStoreEntry? Entry { get; set; }
        public bool FailWrite { get; init; }
        public bool BlockFirstWrite { get; init; }
        public List<string> Writes { get; } = [];
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>直接返回共享对象。</summary><param name="providerId">标识。</param><param name="options">选项。</param><returns>共享条目。</returns>
        public Task<ModelsStoreEntry?> ReadAsync(string providerId, ModelsStoreOperationOptions? options = null) => Task.FromResult(Entry);
        /// <summary>按配置暂停或拒绝写入，故意忽略取消。</summary><param name="providerId">标识。</param><param name="entry">条目。</param><param name="options">选项。</param><returns>写入任务。</returns>
        public async Task WriteAsync(string providerId, ModelsStoreEntry entry, ModelsStoreOperationOptions? options = null)
        {
            if (FailWrite) throw new IOException("Synthetic persistence failure");
            Writes.Add(entry.Models[0].Id);
            if (BlockFirstWrite && Writes.Count == 1) { WriteEntered.TrySetResult(); await ReleaseWrite.Task; }
            Entry = entry;
        }
        /// <summary>删除内存条目。</summary><param name="providerId">标识。</param><param name="options">选项。</param><returns>完成任务。</returns>
        public Task DeleteAsync(string providerId, ModelsStoreOperationOptions? options = null) { Entry = null; return Task.CompletedTask; }
    }

    /// <summary>【AI】【损坏凭据替身】模拟读取失败，其他操作不应执行。</summary>
    private sealed class BrokenCredentialStore : IProviderCredentialStore
    {
        /// <summary>报告读取失败。</summary><param name="providerId">标识。</param><param name="cancellationToken">信号。</param><returns>未使用。</returns>
        public Task<ProviderCredential?> ReadAsync(string providerId, CancellationToken cancellationToken = default) => throw new IOException("Broken credential store");
        /// <summary>禁止元数据读取。</summary><param name="cancellationToken">信号。</param><returns>未使用。</returns>
        public Task<IReadOnlyList<ProviderCredentialInfo>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止修改。</summary><param name="providerId">标识。</param><param name="mutation">修改。</param><param name="cancellationToken">信号。</param><returns>未使用。</returns>
        public Task<ProviderCredential?> ModifyAsync(string providerId, Func<ProviderCredential?, Task<ProviderCredential?>> mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止删除。</summary><param name="providerId">标识。</param><param name="cancellationToken">信号。</param><returns>未使用。</returns>
        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
