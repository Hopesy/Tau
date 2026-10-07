// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【非聊天配置测试】验证配置目录、同名隔离及实际请求参数。</summary>
public sealed class NonChatModelConfigurationTests
{
    private const string Configuration = """
        {"providers":{"mixed":{
          "baseUrl":"https://provider.test/v1","apiKey":"TAU_CAPABILITY_KEY","authHeader":true,
          "headers":{"X-Provider":"TAU_HEADER","X-Shared":"provider"},
          "options":{"timeoutMs":9000,"maxRetries":5,"maxRetryDelayMs":2000,"temperature":0.2,
            "env":{"TAU_CAPABILITY_KEY":"provider-key","TAU_HEADER":"provider-header","PROVIDER_ONLY":"present"},
            "headers":{"X-Option":"provider-option"},"metadata":{"provider":"kept","shared":"provider"}},
          "models":[
            {"id":"same","name":"Chat","api":"openai-chat-completions","headers":{"X-Type":"chat"}},
            {"id":"same","type":"image","name":"Image","api":"openrouter-images","baseUrl":"https://image.test/v1",
             "input":["text","image"],"output":["text","image"],"cost":{"input":1,"output":2},
             "headers":{"X-Type":"image","X-Shared":"image"},
             "options":{"timeoutMs":3000,"maxRetries":2,"metadata":{"shared":"image"},
               "env":{"TAU_CAPABILITY_KEY":"image-key","TAU_HEADER":"image-header"}}},
            {"id":"same","type":"classifier","name":"Classifier","api":"typesafe-system-one","contextWindow":64000,
             "headers":{"X-Type":"classifier","X-Shared":"classifier"},
             "options":{"timeoutMs":4000,"maxRetries":1,"temperature":0.7,"metadata":{"shared":"classifier"},
               "env":{"TAU_CAPABILITY_KEY":"classifier-key","TAU_HEADER":"classifier-header"}}},
            {"id":"future","type":"future","api":"not-supported"}
          ]
        }}}
        """;

    /// <summary>同名自定义模型分别进入正确类型目录，未知类型不落入聊天目录。</summary>
    [Fact]
    public void Catalog_RegistersSameIdPerCapability()
    {
        using var scope = new ConfigurationScope(Configuration);
        var models = BuiltInProviders.CreateBuiltInModels(configurationStore: scope.Configuration);
        var chat = models.GetModel("mixed", "same")!;
        var image = Assert.IsType<ImagesModel>(models.GetModelOfType(ModelTypes.Image, "mixed", "same"));
        var classifier = Assert.IsType<ClassifierModel>(models.GetModelOfType(ModelTypes.Classifier, "mixed", "same"));
        Assert.Equal("Chat", chat.Name);
        Assert.Equal("Image", image.Name);
        Assert.Equal("Classifier", classifier.Name);
        Assert.Equal("https://image.test/v1", image.BaseUrl);
        Assert.Equal("https://provider.test/v1", classifier.BaseUrl);
        Assert.Equal(["text", "image"], image.InputModalities);
        Assert.Equal(["text", "image"], image.OutputModalities);
        Assert.Equal(64000, classifier.ContextWindow);
        Assert.Equal(3, models.GetAllModels("mixed").Count);
        Assert.Single(new ModelCatalog(configurationStore: scope.Configuration).GetModels("mixed"));
    }

    /// <summary>配置使用不同大小写的 provider 名称时仍保留内置分类条目。</summary>
    [Fact]
    public void Catalog_MatchesProviderNamesCaseInsensitively()
    {
        using var scope = new ConfigurationScope("""{"providers":{"TypeSafe":{"baseUrl":"https://override.test"}}}""");
        var models = BuiltInProviders.CreateBuiltInModels(configurationStore: scope.Configuration);
        var model = models.GetModelOfType(ModelTypes.Classifier, "typesafe", "jev-latest");
        Assert.NotNull(model);
        Assert.Equal("https://override.test", model.BaseUrl);
    }

    /// <summary>动态聊天目录刷新保留图像模型，同时重新应用模型覆盖。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Refresh_PreservesConfiguredNonChatModels()
    {
        using var scope = new ConfigurationScope("""
            {"providers":{"radius":{"models":[{"id":"picture","type":"image","api":"openrouter-images"}],
              "modelOverrides":{"fresh":{"name":"Configured fresh"}}}}}
            """);
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"baseUrl":"https://unit.test","models":[{"id":"fresh","name":"Fresh","reasoning":false,"input":["text"],"cost":{},"contextWindow":1000,"maxTokens":100}]}""")
        }));
        using var client = new HttpClient(handler);
        var credentials = new InMemoryProviderCredentialStore();
        await credentials.ModifyAsync("radius", _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.ApiKey(new("fixture-key"))));
        var models = BuiltInProviders.CreateBuiltInModels(configurationStore: scope.Configuration, httpClient: client, credentialStore: credentials);
        var result = await models.RefreshAsync(["radius"]);
        Assert.Empty(result.Errors);
        Assert.Equal("Configured fresh", models.GetModel("radius", "fresh")!.Name);
        Assert.NotNull(models.GetModelOfType(ModelTypes.Image, "radius", "picture"));
    }

    /// <summary>每种类型只读取本类型的自定义请求配置。</summary>
    /// <param name="type">目标模型类型。</param>
    /// <param name="expectedHeader">对应配置请求头。</param>
    [Theory]
    [InlineData(ModelTypes.Chat, "chat")]
    [InlineData(ModelTypes.Image, "image")]
    [InlineData(ModelTypes.Classifier, "classifier")]
    public void RequestConfiguration_IsolatesSameIdByType(string type, string expectedHeader)
    {
        using var scope = new ConfigurationScope(Configuration);
        var models = BuiltInProviders.CreateBuiltInModels(configurationStore: scope.Configuration);
        var model = models.GetModelOfType(type, "mixed", "same")!;
        var config = scope.Configuration.ResolveRequestConfiguration(model);
        Assert.Equal(expectedHeader, config.Headers!["X-Type"]);
        Assert.Equal(expectedHeader == "chat" ? "provider-key" : expectedHeader + "-key", config.ApiKey);
    }

    /// <summary>配置支持缺省选项，显式选项覆盖时不丢失回调和未覆盖的环境字段。</summary>
    /// <param name="image">是否使用图像能力。</param>
    /// <param name="explicitOptions">是否传入显式覆盖。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Dispatch_AppliesConfigurationAndExplicitOverrides(bool image, bool explicitOptions)
    {
        using var scope = new ConfigurationScope(Configuration);
        var type = image ? ModelTypes.Image : ModelTypes.Classifier;
        var model = BuiltInProviders.CreateBuiltInModels(configurationStore: scope.Configuration).GetModelOfType(type, "mixed", "same")!;
        var capture = new CaptureProvider();
        var models = new Models([Definition(capture, model)], configurationStore: scope.Configuration);
        var headers = explicitOptions ? new Dictionary<string, string> { ["x-shared"] = "explicit" } : null;
        var environment = explicitOptions ? new Dictionary<string, string> { ["TAU_HEADER"] = "explicit-header", ["EXPLICIT_ONLY"] = "present" } : null;
        var metadata = explicitOptions ? new Dictionary<string, object> { ["shared"] = "explicit" } : null;
        using var cts = new CancellationTokenSource();
        if (image)
        {
            Func<ProviderResponse, ImagesModel, ValueTask> callback = (_, _) => ValueTask.CompletedTask;
            var options = new ImagesOptions { ApiKey = explicitOptions ? "explicit-key" : null, Headers = headers, Env = environment, Metadata = metadata,
                MaxRetries = explicitOptions ? 0 : null, Timeout = explicitOptions ? TimeSpan.FromSeconds(1) : null, Signal = cts.Token, OnResponse = callback };
            var result = await models.GenerateImagesAsync(model, new ImagesContext([]), options);
            Assert.Equal(ImagesStopReason.Stop, result.StopReason);
            Assert.Same(callback, capture.Images!.OnResponse);
            Assert.Equal(cts.Token, capture.Images.Signal);
            Assert.Same(headers, options.Headers);
        }
        else
        {
            Func<ProviderResponse, ClassifierModel, ValueTask> callback = (_, _) => ValueTask.CompletedTask;
            var options = new ClassifierOptions { ApiKey = explicitOptions ? "explicit-key" : null, Headers = headers, Env = environment, Metadata = metadata,
                MaxRetries = explicitOptions ? 0 : null, Timeout = explicitOptions ? TimeSpan.FromSeconds(1) : null, Temperature = explicitOptions ? 0.9 : null, Signal = cts.Token, OnResponse = callback };
            var result = await models.ClassifyAsync(model, Context(), options);
            Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
            Assert.Same(callback, capture.Classifier!.OnResponse);
            Assert.Equal(cts.Token, capture.Classifier.Signal);
            Assert.Equal(explicitOptions ? 0.9 : 0.7, capture.Classifier.Temperature!.Value, precision: 6);
        }
        Assert.Equal(explicitOptions ? "explicit-key" : type + "-key", capture.Key);
        Assert.Equal("Bearer " + capture.Key, capture.Headers!["Authorization"]);
        Assert.Equal(explicitOptions ? "explicit-header" : type + "-header", capture.Headers["X-Provider"]);
        Assert.Equal(type, capture.Headers["X-Type"]);
        Assert.Equal(explicitOptions ? "explicit" : type, capture.Headers["X-Shared"]);
        Assert.Equal("provider-option", capture.Headers["X-Option"]);
        Assert.Equal(explicitOptions ? 0 : image ? 2 : 1, capture.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(explicitOptions ? 1 : image ? 3 : 4), capture.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(2), capture.MaxRetryDelay);
        Assert.Equal("present", capture.Environment!["PROVIDER_ONLY"]);
        Assert.Equal("kept", ((JsonElement)capture.Metadata!["provider"]).GetString());
        Assert.Equal(explicitOptions ? "explicit" : type, capture.Metadata["shared"].ToString());
        Assert.Equal("TAU_HEADER", model.Headers!["X-Provider"]);
        if (explicitOptions) Assert.Single(headers!);
    }

    /// <summary>自动认证头使用最终解析得到的 key，显式认证头保持最高优先级。</summary>
    /// <param name="explicitHeader">是否显式指定认证头。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatch_UsesResolvedAuthenticationForAutomaticHeader(bool explicitHeader)
    {
        using var scope = new ConfigurationScope(Configuration);
        var model = BuiltInProviders.CreateBuiltInModels(configurationStore: scope.Configuration).GetModelOfType(ModelTypes.Classifier, "mixed", "same")!;
        var capture = new CaptureProvider();
        var auth = new ProviderAuthDefinition(apiKey: new ApiKeyAuthDefinition("test", context =>
        {
            Assert.Equal("classifier-key", context.Credential!.Key);
            Assert.Equal("classifier-header", context.Environment!["TAU_HEADER"]);
            return Task.FromResult<ProviderAuthResult?>(new ProviderAuthResult("resolved-key"));
        }));
        var models = new Models([Definition(capture, model, auth)], configurationStore: scope.Configuration);
        var result = await models.ClassifyAsync(model, Context(), new ClassifierOptions
        {
            Headers = explicitHeader ? new Dictionary<string, string> { ["authorization"] = "Custom explicit" } : null
        });
        Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        Assert.Equal("resolved-key", capture.Key);
        Assert.Equal(explicitHeader ? "Custom explicit" : "Bearer resolved-key", capture.Headers!["Authorization"]);
    }

    /// <summary>有类型的覆盖只影响对应模型，无类型覆盖兼容已有同名模型。</summary>
    /// <param name="typed">是否限制为图像覆盖。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Catalog_OverridesPreserveTypeAndImageOutput(bool typed)
    {
        var type = typed ? "\"type\":\"image\"," : "";
        using var scope = new ConfigurationScope("{\"providers\":{\"p\":{\"baseUrl\":\"https://provider.test\",\"modelOverrides\":{\"same\":{" + type + "\"name\":\"changed\",\"baseUrl\":\"https://override.test\",\"output\":[\"text\",\"image\"]}}}}}");
        var image = new ImagesModel { Id = "same", Name = "original", Provider = "p", Api = "openrouter-images" };
        var classifier = new ClassifierModel { Id = "same", Name = "original", Provider = "p", Api = "typesafe-system-one" };
        var changedImage = Assert.IsType<ImagesModel>(Assert.Single(scope.Configuration.ApplyToModels([image], ModelTypes.Image)));
        var changedClassifier = Assert.IsType<ClassifierModel>(Assert.Single(scope.Configuration.ApplyToModels([classifier], ModelTypes.Classifier)));
        Assert.Equal("changed", changedImage.Name);
        Assert.Equal("https://override.test", changedImage.BaseUrl);
        Assert.Equal(["text", "image"], changedImage.OutputModalities);
        Assert.Equal(typed ? "original" : "changed", changedClassifier.Name);
        Assert.Equal(typed ? "https://provider.test" : "https://override.test", changedClassifier.BaseUrl);
    }

    /// <summary>仅图像或分类的新增 provider 能通过统一入口发送请求，环境 key 来自对应模型。</summary>
    /// <param name="image">是否测试图像协议。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuiltInModels_ExecutesConfiguredNonChatProvider(bool image)
    {
        var type = image ? ModelTypes.Image : ModelTypes.Classifier;
        var api = image ? "openrouter-images" : "typesafe-system-one";
        using var scope = new ConfigurationScope($$$$"""
            {"providers":{"custom":{"baseUrl":"https://custom.test/v1","apiKey":"CUSTOM_MODEL_KEY",
              "models":[{"id":"only","type":"{{{{type}}}}","api":"{{{{api}}}}","options":{"env":{"CUSTOM_MODEL_KEY":"configured-key"}}}]}}}
            """);
        using var handler = new Handler(async request =>
        {
            Assert.Equal(image ? "https://custom.test/v1/chat/completions" : "https://custom.test/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer configured-key", request.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("only", body.RootElement.GetProperty("model").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(image ? "{\"choices\":[{\"message\":{\"content\":\"generated\"}}]}" : "{\"answers\":{}}") };
        });
        using var client = new HttpClient(handler);
        var models = BuiltInProviders.CreateBuiltInModels(configurationStore: scope.Configuration, httpClient: client);
        Assert.Empty(models.GetModels("custom"));
        var model = models.GetModelOfType(type, "custom", "only")!;
        Assert.NotNull(model);
        if (image) Assert.Equal(ImagesStopReason.Stop, (await models.GenerateImagesAsync(model, new ImagesContext([new TextContent("draw")]))).StopReason);
        else Assert.Equal(ClassifierStopReason.Stop, (await models.ClassifyAsync(model, Context())).StopReason);
    }

    /// <summary>构造无问题的分类上下文。</summary>
    /// <returns>独立 JSON 上下文。</returns>
    private static ClassifierContext Context()
    {
        using var doc = JsonDocument.Parse("{}");
        return new(doc.RootElement.Clone(), new Dictionary<string, ClassifierQuestion>());
    }

    /// <summary>建立只捕获非聊天调用的 provider。</summary>
    /// <param name="capture">捕获实现。</param>
    /// <param name="model">模型。</param>
    /// <param name="auth">可选认证定义。</param>
    /// <returns>运行时定义。</returns>
    private static ProviderDefinition Definition(CaptureProvider capture, Model model, ProviderAuthDefinition? auth = null) => new(model.Provider,
        models: [model], auth: auth, images: new Dictionary<string, IImagesProvider> { ["openrouter-images"] = capture },
        classifiers: new Dictionary<string, IClassifierProvider> { ["typesafe-system-one"] = capture });

    private sealed class CaptureProvider : IImagesProvider, IClassifierProvider
    {
        public string Api => "capture";
        public ImagesOptions? Images { get; private set; }
        public ClassifierOptions? Classifier { get; private set; }
        public string? Key => Images?.ApiKey ?? Classifier?.ApiKey;
        public IDictionary<string, string>? Headers => Images?.Headers ?? Classifier?.Headers;
        public int? MaxRetries => Images?.MaxRetries ?? Classifier?.MaxRetries;
        public TimeSpan? Timeout => Images?.Timeout ?? Classifier?.Timeout;
        public TimeSpan? MaxRetryDelay => Images?.MaxRetryDelay ?? Classifier?.MaxRetryDelay;
        public IReadOnlyDictionary<string, string>? Environment => Images?.Env ?? Classifier?.Env;
        public IDictionary<string, object>? Metadata => Images?.Metadata ?? Classifier?.Metadata;
        /// <inheritdoc />
        public Task<AssistantImages> GenerateImagesAsync(ImagesModel model, ImagesContext context, ImagesOptions options)
        {
            Images = options;
            return Task.FromResult(new AssistantImages { Api = model.Api, Model = model.Id, Provider = model.Provider });
        }
        /// <inheritdoc />
        public Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions options)
        {
            Classifier = options;
            return Task.FromResult(new ClassifierResult { Api = model.Api, Model = model.Id, Provider = model.Provider });
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _send;
        /// <summary>注入模拟 HTTP 行为。</summary>
        /// <param name="send">请求处理器。</param>
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => _send = send;
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _send(request);
    }

    private sealed class ConfigurationScope : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "tau-capability-config-" + Guid.NewGuid().ToString("N"));
        public ModelConfigurationStore Configuration { get; }
        /// <summary>创建独立 models.json 配置文件。</summary>
        /// <param name="json">配置 JSON。</param>
        public ConfigurationScope(string json)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "models.json");
            File.WriteAllText(path, json);
            Configuration = new ModelConfigurationStore([path]);
        }
        /// <summary>清理测试创建的独立临时目录。</summary>
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
