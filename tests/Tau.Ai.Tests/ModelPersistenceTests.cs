// 作者：xxx
using System.Text;
using System.Text.Json;
using Tau.Ai.Registry;
using Tau.Ai.Serialization;
using Tau.Ai.Providers.PiMessages;

namespace Tau.Ai.Tests;

/// <summary>【AI】【目录存储测试】验证模型类型、隔离、取消和文件更新完整性。</summary>
public sealed class ModelPersistenceTests
{
    /// <summary>跨存储实例往返保留同名不同类型、嵌套字段及缓存验证元数据。</summary>
    /// <param name="file">是否使用文件存储。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_RoundTripsMixedModelsAndMetadata(bool file)
    {
        using var scope = new StoreScope(file);
        var entry = Entry();
        await scope.Store.WriteAsync("provider", entry);
        var reader = file ? new FileModelsStore(scope.Path) : scope.Store;
        var restored = (await reader.ReadAsync("provider"))!;
        Assert.Equal(3, restored.Models.Count);
        Assert.IsType<Model>(restored.Models[0]);
        Assert.Equal(["text", "image"], Assert.IsType<ImagesModel>(restored.Models[1]).OutputModalities);
        Assert.IsType<ClassifierModel>(restored.Models[2]);
        Assert.Equal(entry.Etag, restored.Etag);
        Assert.Equal(entry.CheckedAt, restored.CheckedAt);
        Assert.Equal(entry.LastModified, restored.LastModified);
        Assert.Equal(1000.5, Assert.Single(restored.Models[0].Cost!.Value.Tiers!).InputTokensAbove);
        Assert.Equal("中文名称", restored.Models[0].Name);
        Assert.Null(restored.Models[0].ThinkingLevelMap!["off"]);
        Assert.Equal("reason", restored.Models[0].ThinkingLevelMap!["high"]);
        Assert.Equal(10m, restored.Models[0].Cost!.Value.Tiers![0].InputPerMillion);
        Assert.Equal("fallback", restored.Models[0].Compat!.AllowedFallbackModels![0].Model);
        Assert.True(((JsonElement)restored.Models[0].SamplingParams!["nested"]).GetProperty("enabled").GetBoolean());
    }

    /// <summary>写入之后和读取之后对嵌套对象的修改均不影响存储内容。</summary>
    /// <param name="file">是否使用文件存储。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_IsolatesNestedMutations(bool file)
    {
        using var scope = new StoreScope(file);
        var entry = Entry();
        await scope.Store.WriteAsync("provider", entry);
        entry.Models[0].Headers!["X-Test"] = "after-write";
        ((string[])((ImagesModel)entry.Models[1]).OutputModalities)[0] = "changed";
        var firstRead = (await scope.Store.ReadAsync("provider"))!;
        Assert.Equal("original", firstRead.Models[0].Headers!["X-Test"]);
        Assert.Equal("text", ((ImagesModel)firstRead.Models[1]).OutputModalities[0]);
        firstRead.Models[0].Headers!["X-Test"] = "after-read";
        var secondRead = (await scope.Store.ReadAsync("provider"))!;
        Assert.Equal("original", secondRead.Models[0].Headers!["X-Test"]);
        Assert.NotSame(firstRead.Models[0].Compat, secondRead.Models[0].Compat);
    }

    /// <summary>缺失读取和删除不会影响其他 provider。</summary>
    /// <param name="file">是否使用文件存储。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_DeletePreservesOtherProviders(bool file)
    {
        using var scope = new StoreScope(file);
        Assert.Null(await scope.Store.ReadAsync("missing"));
        await scope.Store.WriteAsync("one", Entry());
        await scope.Store.WriteAsync("two", Entry() with { Etag = "second" });
        await scope.Store.DeleteAsync("ONE");
        await scope.Store.DeleteAsync("missing");
        Assert.Null(await scope.Store.ReadAsync("one"));
        Assert.Equal("second", (await scope.Store.ReadAsync("two"))!.Etag);
    }

    /// <summary>预先取消的读写删除均不修改已有快照。</summary>
    /// <param name="file">是否使用文件存储。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_PreCancelledOperationsPreserveData(bool file)
    {
        using var scope = new StoreScope(file);
        await scope.Store.WriteAsync("one", Entry());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var options = new ModelsStoreOperationOptions(cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Store.ReadAsync("one", options));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Store.WriteAsync("one", Entry() with { Etag = "changed" }, options));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Store.DeleteAsync("one", options));
        Assert.Equal("\"quoted-etag\"", (await scope.Store.ReadAsync("one"))!.Etag);
    }

    /// <summary>序列化失败不能替换已有快照。</summary>
    /// <param name="file">是否使用文件存储。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_SerializationFailurePreservesPreviousEntry(bool file)
    {
        using var scope = new StoreScope(file);
        var entry = Entry();
        await scope.Store.WriteAsync("one", entry);
        var bad = entry with { Models = [entry.Models[0] with { SamplingParams = new Dictionary<string, object> { ["unsupported"] = new Version(1, 2) } }] };
        await Assert.ThrowsAsync<NotSupportedException>(() => scope.Store.WriteAsync("one", bad));
        Assert.Equal(3, (await scope.Store.ReadAsync("one"))!.Models.Count);
        Assert.Empty(Directory.GetFiles(scope.Directory, "*.tmp"));
    }

    /// <summary>多个文件存储实例并发写入时不丢失其他 provider 的更新。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task FileStore_ConcurrentWritersPreserveEveryProvider()
    {
        using var scope = new StoreScope(true);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => new FileModelsStore(scope.Path).WriteAsync($"p{index}", Entry() with { CheckedAt = index })));
        for (var index = 0; index < 12; index++)
            Assert.Equal(index, (await scope.Store.ReadAsync($"p{index}"))!.CheckedAt);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(scope.Path));
        Assert.Equal(12, document.RootElement.EnumerateObject().Count());
        Assert.Empty(Directory.GetFiles(scope.Directory, "*.tmp"));
    }

    /// <summary>等待独占锁时可取消，取消后的写入不会覆盖旧文件。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task FileStore_LockWaitCanBeCancelled()
    {
        using var scope = new StoreScope(true);
        await scope.Store.WriteAsync("one", Entry());
        using var cts = new CancellationTokenSource();
        await using (var lease = new FileStream(scope.Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var write = scope.Store.WriteAsync("one", Entry() with { Etag = "changed" }, new(cts.Token));
            Assert.False(write.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        Assert.Equal("\"quoted-etag\"", (await scope.Store.ReadAsync("one"))!.Etag);
    }

    /// <summary>损坏的 JSON 不会被写入或删除操作静默覆盖。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task FileStore_CorruptFileIsPreserved()
    {
        using var scope = new StoreScope(true);
        await File.WriteAllTextAsync(scope.Path, "{corrupt");
        await Assert.ThrowsAsync<JsonException>(() => scope.Store.ReadAsync("one"));
        await Assert.ThrowsAsync<JsonException>(() => scope.Store.WriteAsync("one", Entry()));
        await Assert.ThrowsAsync<JsonException>(() => scope.Store.DeleteAsync("one"));
        Assert.Equal("{corrupt", await File.ReadAllTextAsync(scope.Path));
    }

    /// <summary>结构损坏的条目不允许后续写入静默修复并覆盖。</summary>
    /// <param name="json">损坏的存储 JSON。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("{\"one\":null}")]
    [InlineData("{\"one\":{\"models\":null}}")]
    [InlineData("{\"one\":{\"models\":[null]}}")]
    public async Task FileStore_InvalidEntryShapeIsPreserved(string json)
    {
        using var scope = new StoreScope(true);
        await File.WriteAllTextAsync(scope.Path, json);
        await Assert.ThrowsAsync<JsonException>(() => scope.Store.WriteAsync("other", Entry()));
        Assert.Equal(json, await File.ReadAllTextAsync(scope.Path));
    }

    /// <summary>重建 provider 后可以从文件快照加载目录，无需访问网络。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Radius_LoadsPersistedCatalogOfflineAfterRecreation()
    {
        using var scope = new StoreScope(true);
        await scope.Store.WriteAsync("radius", Entry());
        var radius = new RadiusProvider(store: new FileModelsStore(scope.Path));
        var models = await radius.RefreshModelsAsync(allowNetwork: false);
        Assert.Equal(3, models.Count);
        Assert.IsType<ImagesModel>(models[1]);
        Assert.IsType<ClassifierModel>(models[2]);
    }

    /// <summary>读取 BOM 文件，且同一实例能看到外部原子替换后的新版本。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task FileStore_ReadsBomAndExternalUpdates()
    {
        using var scope = new StoreScope(true);
        await scope.Store.WriteAsync("one", Entry());
        var json = await File.ReadAllTextAsync(scope.Path);
        await File.WriteAllTextAsync(scope.Path, json, new UTF8Encoding(true));
        Assert.Equal(3, (await scope.Store.ReadAsync("one"))!.Models.Count);
        await new FileModelsStore(scope.Path).WriteAsync("one", Entry() with { Etag = "external" });
        Assert.Equal("external", (await scope.Store.ReadAsync("one"))!.Etag);
    }

    /// <summary>JSON 中缺少类型仍为聊天，显式图像类型字段可位于末尾。</summary>
    /// <param name="type">附加的类型字段。</param>
    /// <param name="expected">预期能力类型。</param>
    [Theory]
    [InlineData("", ModelTypes.Chat)]
    [InlineData(",\"type\":\"image\",\"outputModalities\":[\"text\",\"image\"]", ModelTypes.Image)]
    [InlineData(",\"type\":\"classifier\"", ModelTypes.Classifier)]
    [InlineData(",\"type\":\"future\"", "future")]
    public void ModelJson_PreservesLegacyAndExplicitTypes(string type, string expected)
    {
        var model = JsonSerializer.Deserialize("{\"id\":\"m\",\"name\":\"M\",\"provider\":\"p\",\"api\":\"a\"" + type + "}", TauAiJsonContext.Default.Model)!;
        Assert.Equal(expected, ModelTypes.GetModelType(model));
        if (expected == ModelTypes.Image) Assert.Equal(["text", "image"], Assert.IsType<ImagesModel>(model).OutputModalities);
        if (expected == ModelTypes.Classifier) Assert.IsType<ClassifierModel>(model);
    }

    /// <summary>构造包含全部常用嵌套字段的混合目录。</summary>
    /// <returns>可用于变更隔离测试的目录条目。</returns>
    private static ModelsStoreEntry Entry()
    {
        var chat = new Model
        {
            Id = "same", Name = "中文名称", Provider = "provider", Api = "chat", Reasoning = true,
            Headers = new Dictionary<string, string> { ["X-Test"] = "original" },
            ThinkingLevelMap = new Dictionary<string, string?> { ["off"] = null, ["high"] = "reason" },
            Cost = new ModelCost(1, 2, Tiers: [new ModelCostTier(10, 20, 1, 2, 1000.5)]),
            SamplingParams = new Dictionary<string, object> { ["nested"] = new Dictionary<string, object> { ["enabled"] = true } },
            Compat = new ModelCompatibility { AllowedFallbackModels = [new("provider", "fallback", new ModelCost(3, 4))] }
        };
        return new ModelsStoreEntry([
            chat,
            new ImagesModel { Id = "same", Name = "Image", Provider = "provider", Api = "image", OutputModalities = new[] { "text", "image" } },
            new ClassifierModel { Id = "same", Name = "Classifier", Provider = "provider", Api = "classifier", ContextWindow = 64000 }
        ], 123, 456, "\"quoted-etag\"");
    }

    private sealed class StoreScope : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-models-store-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "models-store.json");
        public Tau.Ai.Registry.IModelsStore Store { get; }
        /// <summary>建立独立测试目录并选择存储实现。</summary>
        /// <param name="file">是否使用文件存储。</param>
        public StoreScope(bool file)
        {
            System.IO.Directory.CreateDirectory(Directory);
            Store = file ? new FileModelsStore(Path) : new Tau.Ai.Registry.InMemoryModelsStore();
        }
        /// <summary>清理本测试创建的独立临时目录。</summary>
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
