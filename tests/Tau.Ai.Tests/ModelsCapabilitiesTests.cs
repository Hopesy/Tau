// 作者：xxx
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.Ai.Serialization;
using Tau.Ai.Streaming;

namespace Tau.Ai.Tests;

/// <summary>【AI】【模型能力测试】验证混合目录、认证与能力分派边界。</summary>
public sealed class ModelsCapabilitiesTests
{
    private static readonly Model Chat = new() { Id = "same", Name = "Chat", Provider = "mixed", Api = "chat-api" };
    private static readonly ImagesModel Image = new() { Id = "same", Name = "Image", Provider = "mixed", Api = "image-api" };
    private static readonly ClassifierModel Classifier = new() { Id = "same", Name = "Classifier", Provider = "mixed", Api = "classifier-api" };
    private static readonly ClassifierContext Context = new(JsonDocument.Parse("{}").RootElement, new Dictionary<string, ClassifierQuestion>());

    /// <summary>同名模型按类型隔离，未声明类型的旧模型仍视为聊天模型。</summary>
    [Fact]
    public void Catalog_SeparatesSameIdAcrossTypes()
    {
        var provider = CreateDefinition(new CapabilityProvider());
        var models = new Models([provider]);
        Assert.Same(Chat, Assert.Single(models.GetModels()));
        Assert.Same(Chat, models.GetModel("mixed", "same"));
        Assert.Same(Image, models.GetModelOfType(ModelTypes.Image, "mixed", "same"));
        Assert.Same(Classifier, models.GetModelOfType(ModelTypes.Classifier, "mixed", "same"));
        Assert.Equal(3, models.GetAllModels().Count);
        Assert.Single(provider.GetModels());
        Assert.Null(models.GetModelOfType(ModelTypes.Image, "missing", "same"));
        Assert.True(ModelCatalog.ModelsAreEqual(Chat, Chat with { Type = ModelTypes.Chat }));
        Assert.False(ModelCatalog.ModelsAreEqual(Chat, Image));
        Assert.False(ModelCatalog.ModelsAreEqual(Image, Classifier));
    }

    /// <summary>全类型查询和认证过滤保持混合模型的注册顺序。</summary>
    [Fact]
    public void Catalog_PreservesMixedRegistrationOrder()
    {
        var provider = new ProviderDefinition("mixed", new CapabilityProvider(), [Classifier, Chat, Image],
            auth: Auth(new ProviderAuthResult("key")));
        var models = new Models([provider]);
        Assert.Equal([ModelTypes.Classifier, ModelTypes.Chat, ModelTypes.Image], models.GetAllModels().Select(ModelTypes.GetModelType));
        Assert.Equal([ModelTypes.Classifier, ModelTypes.Chat, ModelTypes.Image], models.GetAllAvailable().Select(ModelTypes.GetModelType));
    }

    /// <summary>旧 provider 的虚拟聊天查询仍被统一目录调用，损坏目录按 best-effort 隔离。</summary>
    [Fact]
    public void Catalog_PreservesLegacyOverridesAndIsolatesBrokenProvider()
    {
        var models = new Models([new LegacyDefinition(false), new LegacyDefinition(true)]);
        Assert.Same(Chat, Assert.Single(models.GetAllModels()));
        Assert.Same(Chat, Assert.Single(models.GetModels()));
        Assert.Null(models.GetModel("broken", "same"));
    }

    /// <summary>初始和刷新目录丢弃未知能力，保留有效模型。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Catalog_FiltersUnknownTypesOnRefresh()
    {
        var unknown = Chat with { Type = "future" };
        var provider = new ProviderDefinition("mixed", new CapabilityProvider(), [Chat, unknown],
            refreshModels: _ => Task.FromResult<IReadOnlyList<Model>>([Image, Classifier, unknown]));
        var models = new Models([provider]);
        Assert.Single(models.GetAllModels());
        var refreshed = await models.RefreshAsync();
        Assert.Empty(refreshed.Errors);
        Assert.Empty(models.GetModels());
        Assert.Equal(2, models.GetAllModels().Count);
    }

    /// <summary>同步和异步可用查询均排除没有认证的全部能力模型。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Availability_ExcludesUnconfiguredProviders()
    {
        var models = new Models([CreateDefinition(new CapabilityProvider(), Auth(null))]);
        Assert.Empty(models.GetAvailable());
        Assert.Empty(models.GetAllAvailable());
        Assert.Empty(await models.GetAvailableAsync());
        Assert.Empty(await models.GetAllAvailableAsync());
    }

    /// <summary>旧聊天过滤器不误删同名图像和分类条目。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Availability_ChatFilterLeavesOtherTypesAvailable()
    {
        var provider = new ProviderDefinition("mixed", new CapabilityProvider(), [Chat, Image, Classifier],
            auth: Auth(new ProviderAuthResult("key")), filterModels: _ => []);
        var models = new Models([provider]);
        Assert.Empty(models.GetAvailable());
        Assert.Equal(2, models.GetAllAvailable().Count);
        Assert.Single(models.GetAvailableOfType(ModelTypes.Image));
        Assert.Single(await models.GetAvailableOfTypeAsync(ModelTypes.Classifier));
        Assert.Equal(2, (await models.GetAllAvailableAsync()).Count);
    }

    /// <summary>全类型凭据过滤器覆盖旧聊天过滤器，并接收实际存储凭据。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Availability_AllFilterReceivesCredentialAndOverridesChatFilter()
    {
        var store = new InMemoryProviderCredentialStore();
        var credential = new ProviderCredential.ApiKey(new ApiKeyCredential("stored"));
        await store.ModifyAsync("mixed", _ => Task.FromResult<ProviderCredential?>(credential));
        var provider = new ProviderDefinition("mixed", new CapabilityProvider(), [Chat, Image, Classifier],
            auth: Auth(new ProviderAuthResult("key")), filterModels: _ => [],
            filterAllModels: (all, actual) => { Assert.Equal(credential, actual); return all.Where(model => model is ClassifierModel).ToArray(); });
        var models = new Models([provider], credentialStore: store);
        Assert.Same(Classifier, Assert.Single(models.GetAllAvailable()));
        Assert.Same(Classifier, Assert.Single(await models.GetAllAvailableAsync()));
    }

    /// <summary>图像和分类请求合并默认值、认证及显式配置，保持调用方原始模型不变。</summary>
    /// <param name="image">是否测试图像能力。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dispatch_MergesAuthenticationAndDefaults(bool image)
    {
        var implementation = new CapabilityProvider();
        var resolutions = 0;
        var auth = new ProviderAuthDefinition(apiKey: new ApiKeyAuthDefinition("test", context =>
        {
            resolutions++;
            Assert.Equal("explicit", context.Credential?.Key);
            return Task.FromResult<ProviderAuthResult?>(new ProviderAuthResult("resolved",
                new Dictionary<string, string> { ["X-Shared"] = "auth", ["X-Auth"] = "auth" },
                "https://auth.test", new Dictionary<string, string> { ["AUTH_ENV"] = "auth" }));
        }));
        var provider = CreateDefinition(implementation, auth);
        var models = new Models([provider]);
        Model model = image ? Image : Classifier;
        model = model with { Headers = new Dictionary<string, string> { ["X-Model"] = "model", ["X-Shared"] = "model" } };
        var headers = new Dictionary<string, string> { ["x-shared"] = "request" };
        if (image)
        {
            var result = await models.GenerateImagesAsync(model, new ImagesContext([]), new ImagesOptions { ApiKey = "explicit", Headers = headers });
            Assert.Equal(ImagesStopReason.Stop, result.StopReason);
        }
        else
        {
            var result = await models.ClassifyAsync(model, Context, new ClassifierOptions { ApiKey = "explicit", Headers = headers });
            Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        }
        Assert.Equal(1, resolutions);
        Assert.Equal("resolved", implementation.Key);
        Assert.Equal("https://auth.test", implementation.Model!.BaseUrl);
        Assert.Equal("default", implementation.Model.Headers!["X-Default"]);
        Assert.Equal("model", implementation.Model.Headers["X-Model"]);
        Assert.Equal("request", implementation.Headers!["X-Shared"]);
        Assert.Equal("auth", implementation.Headers["X-Auth"]);
        Assert.Null(model.BaseUrl);
        Assert.False(model.Headers!.ContainsKey("X-Default"));
    }

    /// <summary>错误类型不能进入另一能力的实现。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Dispatch_RejectsWrongTypesBeforeProviderExecution()
    {
        var implementation = new CapabilityProvider();
        var models = new Models([CreateDefinition(implementation)]);
        Assert.Equal(ClassifierStopReason.Error, (await models.ClassifyAsync(Chat, Context)).StopReason);
        Assert.Equal(ImagesStopReason.Error, (await models.GenerateImagesAsync(Classifier, new ImagesContext([]))).StopReason);
        Assert.Equal(StopReason.Error, (await models.CompleteAsync(Image, new LlmContext(null, [], []), new StreamOptions())).StopReason);
        Assert.Equal(StopReason.Error, (await models.CompleteSimpleAsync(Classifier, new LlmContext(null, [], []), new SimpleStreamOptions())).StopReason);
        Assert.Equal(0, implementation.Calls);
    }

    /// <summary>不存在的 provider 和不支持的 API 均返回完整错误结果。</summary>
    /// <param name="missingProvider">是否使用未知 provider。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dispatch_ReturnsErrorsForMissingCapabilities(bool missingProvider)
    {
        var models = new Models([new ProviderDefinition("mixed", new CapabilityProvider(), [Chat])]);
        var model = Classifier with { Provider = missingProvider ? "unknown" : "mixed" };
        var result = await models.ClassifyAsync(model, Context);
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Equal(model.Provider, result.Provider);
        Assert.Equal(model.Id, result.Model);
        Assert.Empty(result.Answers);
        Assert.NotEmpty(result.ErrorMessage!);
    }

    /// <summary>预先取消不触发实现；实现抛出异常时返回错误结果。</summary>
    /// <param name="cancel">是否预先取消。</param>
    /// <param name="image">是否测试图像能力。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Dispatch_EncodesCancellationAndExceptions(bool cancel, bool image)
    {
        var implementation = new CapabilityProvider { Throw = true };
        var models = new Models([CreateDefinition(implementation)]);
        using var cts = new CancellationTokenSource();
        if (cancel) cts.Cancel();
        if (image)
            Assert.Equal(cancel ? ImagesStopReason.Aborted : ImagesStopReason.Error,
                (await models.GenerateImagesAsync(Image, new ImagesContext([]), new ImagesOptions { Signal = cts.Token })).StopReason);
        else
            Assert.Equal(cancel ? ClassifierStopReason.Aborted : ClassifierStopReason.Error,
                (await models.ClassifyAsync(Classifier, Context, new ClassifierOptions { Signal = cts.Token })).StopReason);
        Assert.Equal(cancel ? 0 : 1, implementation.Calls);
    }

    /// <summary>只声明非聊天能力的 provider 可注册，空实现定义被拒绝。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Provider_AllowsClassifierOnlyAndRejectsNoImplementations()
    {
        Assert.Throws<ArgumentException>(() => new ProviderDefinition("empty"));
        var provider = new ProviderDefinition("mixed", models: [Classifier], classifiers: new Dictionary<string, IClassifierProvider> { [Classifier.Api] = new CapabilityProvider() });
        var models = new Models([provider]);
        Assert.Empty(models.GetModels());
        Assert.Equal(ClassifierStopReason.Stop, (await models.ClassifyAsync(Classifier, Context)).StopReason);
        Assert.Equal(StopReason.Error, (await models.CompleteAsync(Chat, new LlmContext(null, [], []), new StreamOptions())).StopReason);
    }

    /// <summary>内置统一目录包含图像和分类，但不把它们暴露为聊天模型。</summary>
    [Fact]
    public void BuiltIns_ExposeImagesAndClassifiers()
    {
        var models = BuiltInProviders.CreateBuiltInModels();
        Assert.NotEmpty(models.GetModelsOfType(ModelTypes.Image, "openrouter"));
        Assert.Empty(models.GetModels("typesafe"));
        Assert.Equal(64_000, models.GetModelOfType(ModelTypes.Classifier, "typesafe", "jev-latest")!.ContextWindow);
        Assert.Equal(2, models.GetModelsOfType(ModelTypes.Classifier, "opencode").Count);
        Assert.Equal("test-key", EnvironmentApiKeyResolver.GetApiKey("typesafe", new Dictionary<string, string> { ["TYPESAFE_API_KEY"] = "test-key" }));
    }

    /// <summary>认证缺失或解析异常时，图像与分类都返回失败且不执行协议。</summary>
    /// <param name="throws">认证解析是否抛出异常。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatch_StopsAtAuthenticationFailure(bool throws)
    {
        var implementation = new CapabilityProvider();
        var auth = new ProviderAuthDefinition(apiKey: new ApiKeyAuthDefinition("test", _ =>
            throws ? throw new InvalidOperationException("auth failed") : Task.FromResult<ProviderAuthResult?>(null)));
        var models = new Models([CreateDefinition(implementation, auth)]);
        Assert.Equal(ImagesStopReason.Error, (await models.GenerateImagesAsync(Image, new ImagesContext([]))).StopReason);
        Assert.Equal(ClassifierStopReason.Error, (await models.ClassifyAsync(Classifier, Context)).StopReason);
        Assert.Equal(0, implementation.Calls);
    }

    /// <summary>分类判别联合使用源生成元数据完成 JSON 往返。</summary>
    [Fact]
    public void ClassifierJson_RoundTripsPolymorphicQuestionsAndAnswers()
    {
        var context = Context with { Questions = new Dictionary<string, ClassifierQuestion> { ["ok"] = new ClassifierBoolQuestion("判断", new("成立", "不成立")) } };
        var json = JsonSerializer.Serialize(context, TauAiJsonContext.Default.ClassifierContext);
        var restored = JsonSerializer.Deserialize(json, TauAiJsonContext.Default.ClassifierContext)!;
        Assert.Equal("成立", Assert.IsType<ClassifierBoolQuestion>(restored.Questions["ok"]).Criteria.True);
        var result = new ClassifierResult { Api = "a", Provider = "p", Model = "m", Answers = new Dictionary<string, ClassifierAnswer> { ["ok"] = new ClassifierBoolAnswer(0.8) } };
        var resultJson = JsonSerializer.Serialize(result, TauAiJsonContext.Default.ClassifierResult);
        Assert.Equal(0.8, Assert.IsType<ClassifierBoolAnswer>(JsonSerializer.Deserialize(resultJson, TauAiJsonContext.Default.ClassifierResult)!.Answers["ok"]).Probability);
    }

    /// <summary>创建包含三个能力及默认配置的 provider。</summary>
    /// <param name="implementation">模拟实现。</param>
    /// <param name="auth">可选认证定义。</param>
    /// <returns>provider 定义。</returns>
    private static ProviderDefinition CreateDefinition(CapabilityProvider implementation, ProviderAuthDefinition? auth = null) =>
        new("mixed", implementation, [Chat, Image, Classifier], auth: auth, baseUrl: "https://default.test",
            headers: new Dictionary<string, string> { ["X-Default"] = "default" },
            images: new Dictionary<string, IImagesProvider> { [Image.Api] = implementation },
            classifiers: new Dictionary<string, IClassifierProvider> { [Classifier.Api] = implementation });

    /// <summary>创建返回固定认证结果的定义。</summary>
    /// <param name="result">认证结果。</param>
    /// <returns>认证定义。</returns>
    private static ProviderAuthDefinition Auth(ProviderAuthResult? result) => new(apiKey: new ApiKeyAuthDefinition("test", _ => Task.FromResult(result)));

    private sealed class LegacyDefinition : ProviderDefinition
    {
        private readonly bool _broken;
        /// <summary>创建仅重写旧目录查询的实现。</summary>
        /// <param name="broken">是否模拟读取失败。</param>
        public LegacyDefinition(bool broken) : base(broken ? "broken" : "legacy", new CapabilityProvider()) => _broken = broken;
        /// <inheritdoc />
        public override IReadOnlyList<Model> GetModels() => _broken ? throw new InvalidOperationException("broken") : [Chat];
    }

    private sealed class CapabilityProvider : IStreamProvider, IImagesProvider, IClassifierProvider
    {
        public string Api => "test";
        public int Calls { get; private set; }
        public bool Throw { get; init; }
        public Model? Model { get; private set; }
        public string? Key { get; private set; }
        public IDictionary<string, string>? Headers { get; private set; }
        /// <inheritdoc />
        public Task<AssistantImages> GenerateImagesAsync(ImagesModel model, ImagesContext context, ImagesOptions options)
        {
            Capture(model, options.ApiKey, options.Headers);
            return Task.FromResult(new AssistantImages { Api = model.Api, Provider = model.Provider, Model = model.Id });
        }
        /// <inheritdoc />
        public Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions options)
        {
            Capture(model, options.ApiKey, options.Headers);
            return Task.FromResult(new ClassifierResult { Api = model.Api, Provider = model.Provider, Model = model.Id });
        }
        /// <inheritdoc />
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            Capture(model, options.ApiKey, options.Headers);
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage { StopReason = StopReason.EndTurn }));
            return stream;
        }
        /// <inheritdoc />
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Stream(model, context, options);
        /// <summary>保存请求参数并可选模拟实现异常。</summary>
        /// <param name="model">模型。</param>
        /// <param name="key">认证 key。</param>
        /// <param name="headers">请求头。</param>
        private void Capture(Model model, string? key, IDictionary<string, string>? headers)
        {
            Calls++;
            Model = model;
            Key = key;
            Headers = headers;
            if (Throw) throw new InvalidOperationException("provider failed");
        }
    }
}
