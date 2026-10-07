// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Providers.PiMessages;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【Radius】【动态目录测试】验证 SDK 缓存、旧凭据导入、认证联网与发布失败保护。</summary>
public sealed class RadiusModelsRefreshTests
{
    /// <summary>【Radius】【离线恢复】重建集合后恢复当前提供方缓存，其他提供方及大小写不同的标识被过滤。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task OfflineRefresh_RestoresCacheAndKeepsBaseline()
    {
        var store = new InMemoryModelsStore();
        await store.WriteAsync("radius", new([Model("cached"), Model("foreign") with { Provider = "other" }, Model("wrong-case") with { Provider = "Radius" }], CheckedAt: 42));
        using var client = Client(_ => throw new InvalidOperationException("Offline must not request HTTP"));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var models = Create(client, store); var baseline = models.GetModels("radius").Select(model => model.Id).ToArray();
            Assert.NotEmpty(baseline);
            Assert.Empty((await models.RefreshAsync(["radius"], allowNetwork: false)).Errors);
            Assert.NotNull(models.GetModel("radius", "cached")); Assert.Null(models.GetModel("radius", "foreign")); Assert.Null(models.GetModel("radius", "wrong-case"));
            Assert.All(baseline, id => Assert.NotNull(models.GetModel("radius", id)));
        }
        Assert.Equal(42, (await store.ReadAsync("radius"))!.CheckedAt);
    }

    /// <summary>【Radius】【旧目录导入】无缓存时迁入旧 OAuth 目录，已有缓存包括空目录均优先。</summary>
    /// <param name="stored">是否已有缓存。</param><param name="empty">已有缓存是否为空。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyOAuthCatalog_IsImportedOnlyWhenStoreIsMissing(bool stored, bool empty)
    {
        var store = new InMemoryModelsStore();
        if (stored) await store.WriteAsync("radius", new(empty ? [] : [Model("cached")], CheckedAt: 42));
        var credentials = await Credentials(OAuth(Config("legacy")));
        using var client = Client(_ => throw new InvalidOperationException("Offline must not request HTTP"));
        var models = Create(client, store, credentials);
        Assert.Empty((await models.RefreshAsync(["radius"], allowNetwork: false)).Errors);
        var entry = (await store.ReadAsync("radius"))!;
        if (stored)
        {
            Assert.Equal(42, entry.CheckedAt); Assert.Null(models.GetModel("radius", "legacy"));
            Assert.Equal(empty ? 0 : 1, entry.Models.Count);
        }
        else
        {
            Assert.Equal("legacy", Assert.Single(entry.Models).Id); Assert.True(entry.CheckedAt > 42);
            Assert.Equal("https://stream.invalid", models.GetModel("radius", "legacy")!.BaseUrl);
        }
    }

    /// <summary>【Radius】【空旧目录】格式无效或没有有效条目的旧目录不会生成新的缓存。</summary>
    /// <param name="config">旧目录 JSON。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("null")]
    [InlineData("{\"baseUrl\":\"https://x.invalid\",\"models\":[]}")]
    [InlineData("{\"baseUrl\":\"https://x.invalid\",\"models\":[{}]}")]
    public async Task InvalidOrEmptyLegacyCatalog_DoesNotCreateCache(string config)
    {
        var store = new InMemoryModelsStore(); using var client = Client(_ => throw new InvalidOperationException());
        var models = Create(client, store, await Credentials(OAuth(config)));
        Assert.Empty((await models.RefreshAsync(["radius"], allowNetwork: false)).Errors);
        Assert.Null(await store.ReadAsync("radius"));
    }

    /// <summary>【Radius】【认证联网】联网使用当前 API key 或 OAuth access，并在保存动态目录后合并内置基线。</summary>
    /// <param name="oauth">是否使用 OAuth。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkRefresh_PersistsDynamicModelsAndUsesCredential(bool oauth)
    {
        var store = new InMemoryModelsStore(); var calls = 0;
        using var client = Client(request =>
        {
            Assert.Equal("https://radius.pi.dev/v1/config", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer " + (oauth ? "oauth-access" : "fixture-key"), request.Headers.Authorization!.ToString());
            calls++; return Task.FromResult(Response(Config("fresh")));
        });
        var credentials = await Credentials(oauth ? OAuth(Config("legacy")) : new ProviderCredential.ApiKey(new("fixture-key")));
        var models = Create(client, store, credentials); var baseline = models.GetModels("radius").Select(model => model.Id).ToArray();
        Assert.Empty((await models.RefreshAsync(["radius"])).Errors);
        Assert.Equal(1, calls); Assert.NotNull(models.GetModel("radius", "fresh")); Assert.Null(models.GetModel("radius", "legacy"));
        Assert.All(baseline, id => Assert.NotNull(models.GetModel("radius", id)));
        Assert.Equal("fresh", Assert.Single((await store.ReadAsync("radius"))!.Models).Id);
    }

    /// <summary>【Radius】【目录合并】大小写敏感覆盖，重复动态条目后者胜出且不改变原位置。</summary>
    [Fact]
    public void Merge_OverridesByExactIdAndPreservesOrder()
    {
        var merged = RadiusProvider.MergeModels([Model("same"), Model("keep")],
            [Model("same") with { Name = "first" }, Model("Same"), Model("new"), Model("same") with { Name = "last" }]);
        Assert.Equal(new[] { "same", "keep", "Same", "new" }, merged.Select(model => model.Id));
        Assert.Equal("last", merged[0].Name);
    }

    /// <summary>【Radius】【联网失败】HTTP 错误保留已恢复的缓存目录与保存时间。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task NetworkFailure_KeepsRestoredCache()
    {
        var store = new InMemoryModelsStore(); await store.WriteAsync("radius", new([Model("cached")], CheckedAt: 42));
        using var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("unavailable") }));
        var models = Create(client, store, await Credentials(new ProviderCredential.ApiKey(new("fixture-key"))));
        Assert.Contains("502", (await models.RefreshAsync(["radius"])).Errors["radius"].Message);
        Assert.NotNull(models.GetModel("radius", "cached")); Assert.Equal(42, (await store.ReadAsync("radius"))!.CheckedAt);
    }

    /// <summary>【Radius】【过期响应】忽略取消的旧 HTTP 操作不能覆盖新目录或缓存。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task SupersededNetworkRefresh_CannotOverwriteNewCatalog()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var client = Client(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); return release.Task; }
            return Task.FromResult(Response(Config("new")));
        });
        var store = new InMemoryModelsStore();
        var models = Create(client, store, await Credentials(new ProviderCredential.ApiKey(new("fixture-key"))));
        var first = models.RefreshAsync(["radius"]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty((await models.RefreshAsync(["radius"])).Errors);
            Assert.Empty((await first.WaitAsync(TimeSpan.FromSeconds(5))).Errors);
        }
        finally { release.TrySetResult(Response(Config("old"))); }
        Assert.NotNull(models.GetModel("radius", "new")); Assert.Null(models.GetModel("radius", "old"));
        Assert.Equal("new", Assert.Single((await store.ReadAsync("radius"))!.Models).Id);
    }

    /// <summary>【Radius】【拒绝发布】缓存恢复发布被拒绝时不执行后续网络操作。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RejectedRestoration_StopsNetworkPhase()
    {
        using var client = Client(_ => throw new InvalidOperationException("Rejected publication must stop HTTP"));
        var provider = new RadiusProvider(httpClient: client); var calls = 0;
        await provider.RefreshModelsAsync("radius", new(null, new([Model("cached")]), true, null, default, _ =>
        { calls++; return Task.FromResult(false); }), _ => throw new InvalidOperationException("Rejected update"));
        Assert.Equal(1, calls); Assert.Empty(provider.Models);
    }

    /// <summary>【Radius】【存储失败】保存远端目录失败时不替换已公开的目录。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task FailedPersistence_DoesNotReplaceRestoredCatalog()
    {
        using var client = Client(_ => Task.FromResult(Response(Config("fresh"))));
        var store = new FailingStore();
        var models = Create(client, store, await Credentials(new ProviderCredential.ApiKey(new("fixture-key"))));
        Assert.IsType<IOException>((await models.RefreshAsync(["radius"])).Errors["radius"]);
        Assert.NotNull(models.GetModel("radius", "cached")); Assert.Null(models.GetModel("radius", "fresh"));
    }

    /// <summary>【Radius】【测试集合】用空文件来源和注入存储隔离真实用户配置。</summary>
    /// <param name="client">模拟 HTTP。</param><param name="store">目录存储。</param><param name="credentials">可选凭据。</param><returns>内置集合。</returns>
    private static Models Create(HttpClient client, Registry.ModelsStore store, IProviderCredentialStore? credentials = null)
    {
        var configuration = new ModelConfigurationStore([]);
        return BuiltInProviders.CreateBuiltInModels(configuration, new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: configuration), client, credentials, store);
    }

    /// <summary>【Radius】【测试凭据】保存合成凭据。</summary><param name="value">凭据。</param><returns>内存存储。</returns>
    private static async Task<InMemoryProviderCredentialStore> Credentials(ProviderCredential value)
    {
        var store = new InMemoryProviderCredentialStore(); await store.ModifyAsync("radius", _ => Task.FromResult<ProviderCredential?>(value)); return store;
    }

    /// <summary>【Radius】【测试 OAuth】构造携带旧目录的未过期合成凭据。</summary><param name="config">配置。</param><returns>凭据。</returns>
    private static ProviderCredential OAuth(string config)
    {
        using var document = JsonDocument.Parse(config);
        return new ProviderCredential.OAuth(new("refresh", "oauth-access", DateTimeOffset.MaxValue)
        { Properties = new Dictionary<string, JsonElement> { ["gatewayConfig"] = document.RootElement.Clone() } });
    }

    /// <summary>【Radius】【测试目录】生成有效的单模型配置。</summary><param name="id">标识。</param><returns>JSON。</returns>
    private static string Config(string id) => $$$$"""{"baseUrl":"https://stream.invalid","models":[{"id":"{{{{id}}}}","name":"Model","reasoning":false,"input":["text"],"cost":{},"contextWindow":1000,"maxTokens":100}]}""";

    /// <summary>【Radius】【测试模型】生成缓存条目。</summary><param name="id">标识。</param><returns>模型。</returns>
    private static Model Model(string id) => new() { Id = id, Name = id, Provider = "radius", Api = "pi-messages", BaseUrl = "https://cached.invalid", ContextWindow = 1000, MaxOutputTokens = 100 };

    /// <summary>【Radius】【测试 HTTP】构造成功响应。</summary><param name="json">正文。</param><returns>响应。</returns>
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    /// <summary>【Radius】【测试 HTTP】构造完全模拟客户端。</summary><param name="send">响应委托。</param><returns>客户端。</returns>
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(new Handler(send));

    /// <summary>【Radius】【测试传输】按委托返回响应。</summary><param name="send">响应委托。</param>
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    /// <summary>【Radius】【测试存储】可恢复缓存但写入失败。</summary>
    private sealed class FailingStore : Registry.ModelsStore
    {
        /// <inheritdoc />
        public Task<ModelsStoreEntry?> ReadAsync(string providerId, ModelsStoreOperationOptions? options = null) => Task.FromResult<ModelsStoreEntry?>(new([Model("cached")]));
        /// <inheritdoc />
        public Task WriteAsync(string providerId, ModelsStoreEntry entry, ModelsStoreOperationOptions? options = null) => throw new IOException("Fixture persistence failure");
        /// <inheritdoc />
        public Task DeleteAsync(string providerId, ModelsStoreOperationOptions? options = null) => Task.CompletedTask;
    }
}
