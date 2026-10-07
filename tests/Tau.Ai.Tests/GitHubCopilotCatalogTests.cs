// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth.OAuth.Providers;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Copilot 账户目录测试】验证 picker/policy、限速预算、启用流程及 SDK 权限过滤。</summary>
public sealed class GitHubCopilotCatalogTests
{
    private const string Device = """{"device_code":"device","user_code":"USER","verification_uri":"https://github.com/login/device","interval":1,"expires_in":120}""";
    private const string Token = """{"token":"access","expires_at":2000000000}""";

    /// <summary>【AI】【picker 优先】工具调用被禁止的模型排除，picker 开启且未 disabled 的模型可用，未知 policy 模型不自动启用。</summary>
    [Fact]
    public void Catalog_FiltersToolsAndPolicies()
    {
        using var document = JsonDocument.Parse("""
            {"data":[null,{}, {"id":"disabled","model_picker_enabled":true,"policy":{"state":"disabled"}},
             {"id":"no-tools","model_picker_enabled":true,"capabilities":{"supports":{"tool_calls":false}}},
             {"id":"selected","model_picker_enabled":true}, {"id":"hidden","policy":{"state":"enabled"}},
             {"id":"known","model_picker_enabled":true,"policy":{"state":"unconfigured"}},
             {"id":"unknown","model_picker_enabled":true,"policy":{"state":"unconfigured"}}]}
            """);
        var catalog = GitHubCopilotOAuthProvider.ParseCatalog(document.RootElement, true, new HashSet<string> { "known" });
        Assert.Equal(new[] { "selected", "known", "unknown" }, catalog.AvailableModelIds); Assert.Equal("known", Assert.Single(catalog.PolicyModelIds));
    }

    /// <summary>【AI】【个人账户回退】只有指定个人端点且所有 picker 不可用时，才依据 policy 允许目录。</summary><param name="individual">个人端点。</param><returns>无返回值。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Catalog_UsesPolicyFallbackOnlyForIndividual(bool individual)
    {
        using var document = JsonDocument.Parse("""{"data":[{"id":"enabled","policy":{"state":"enabled"}},{"id":"known","policy":{"state":"unconfigured"}}]}""");
        var catalog = GitHubCopilotOAuthProvider.ParseCatalog(document.RootElement, individual, new HashSet<string> { "known" });
        Assert.Equal(individual ? new[] { "enabled" } : [], catalog.AvailableModelIds); Assert.Equal(individual ? new[] { "known" } : [], catalog.PolicyModelIds);
    }

    /// <summary>【AI】【目录格式】缺少 data 数组的响应不能作为有效账户目录。</summary><param name="json">响应。</param>
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    public void Catalog_RejectsInvalidRoot(string json)
    {
        using var document = JsonDocument.Parse(json); Assert.Equal("Invalid Copilot models response", Assert.Throws<InvalidOperationException>(() => GitHubCopilotOAuthProvider.ParseCatalog(document.RootElement, false, new HashSet<string>())).Message);
    }

    /// <summary>【AI】【重试等待】解析秒数前缀、负数和 HTTP 日期，非法或无穷等待停止重试。</summary><param name="header">响应头。</param><param name="expected">等待毫秒。</param>
    [Theory]
    [InlineData(null, 500.0)]
    [InlineData("0.25", 250.0)]
    [InlineData("2 seconds", 2000.0)]
    [InlineData("-1", 0.0)]
    [InlineData("1e2", 100000.0)]
    [InlineData("Thu, 01 Jan 1970 00:00:02 GMT", 1000.0)]
    [InlineData("garbage", null)]
    [InlineData("Infinity", null)]
    public void RetryAfter_ParsesExpectedDelay(string? header, double? expected) => Assert.Equal(expected, GitHubCopilotOAuthProvider.ParseRetryDelay(header, 500, 1000));

    /// <summary>【AI】【登录目录重试】登录目录最多重试两次，保留协议版本、客户端头和有效等待。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Login_RetriesCatalogAndPersistsModelIds()
    {
        var catalogCalls = 0; var fixture = new Fixture(request =>
        {
            Assert.Equal("2026-06-01", request.Headers.GetValues("X-GitHub-Api-Version").Single());
            Assert.Equal("Bearer access", request.Headers.Authorization!.ToString());
            return ++catalogCalls < 3 ? Response(429, "limited") : Response(200, """{"data":[{"id":"ready","model_picker_enabled":true},{"id":"ready","model_picker_enabled":true}]}""");
        });
        using (fixture)
        {
            var result = await fixture.Provider.LoginAsync(fixture.Callbacks);
            Assert.Equal(3, catalogCalls); Assert.Equal(new[] { 1000.0, 500.0, 1000.0 }, fixture.Delays);
            Assert.Equal(new[] { "ready" }, Ids(result));
        }
    }

    /// <summary>【AI】【刷新目录】刷新不重试 429，目录失败会使整个刷新失败。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Refresh_DoesNotRetryCatalogFailure()
    {
        var calls = 0; using var fixture = new Fixture(_ => { calls++; return Response(429, "limited"); });
        Assert.Contains("429", (await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.RefreshTokenAsync(Old()))).Message);
        Assert.Equal(1, calls); Assert.Empty(fixture.Delays);
    }

    /// <summary>【AI】【刷新权限】刷新仅获取账户目录，不主动启用模型，原始重复 ID 也按上游保留。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Refresh_PreservesCatalogIdsWithoutPolicyMutation()
    {
        using var fixture = new Fixture(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Response(200, """{"data":[{"id":"known","model_picker_enabled":true,"policy":{"state":"unconfigured"}},{"id":"known","model_picker_enabled":true}]}""");
        }, ["known"]);
        var result = await fixture.Provider.RefreshTokenAsync(Old()); Assert.Equal(new[] { "known", "known" }, Ids(result)); Assert.Empty(fixture.Callbacks.Progress);
    }

    /// <summary>【AI】【启用顺序】普通错误跳过，成功 ID 合入目录，耗尽 429 预算后停止后续模型。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Login_EnablesKnownModelsAndStopsAtRateLimit()
    {
        var policies = new List<string>();
        using var fixture = new Fixture(request =>
        {
            if (request.Method == HttpMethod.Get) return Response(200, """
                {"data":[{"id":"ready","policy":{"state":"enabled"}},
                 {"id":"fail","policy":{"state":"unconfigured"}}, {"id":"success","policy":{"state":"unconfigured"}},
                 {"id":"limited","policy":{"state":"unconfigured"}}, {"id":"never","policy":{"state":"unconfigured"}}]}
                """);
            var id = request.RequestUri!.Segments[^2].Trim('/'); policies.Add(id);
            Assert.Equal("chat-policy", request.Headers.GetValues("openai-intent").Single()); Assert.Equal("chat-policy", request.Headers.GetValues("x-interaction-type").Single());
            Assert.Empty(request.Headers.Accept); Assert.False(request.Headers.Contains("X-GitHub-Api-Version"));
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.ToString());
            if (id == "limited") { var limited = Response(429, "limited"); limited.Headers.TryAddWithoutValidation("Retry-After", "100"); return limited; }
            return Response(id == "success" ? 200 : 500, "{}");
        }, ["fail", "success", "limited", "never"]);
        var result = await fixture.Provider.LoginAsync(fixture.Callbacks);
        Assert.Equal(new[] { "fail", "success", "limited" }, policies); Assert.Equal(new[] { "ready", "success" }, Ids(result));
        Assert.Equal("Enabling models...", Assert.Single(fixture.Callbacks.Progress));
    }

    /// <summary>【AI】【账户端点】企业账户不能因 picker 为空而回退到个人账户的 enabled policy。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task EnterpriseCatalog_DoesNotUseIndividualFallback()
    {
        using var fixture = new Fixture(request =>
        { Assert.Equal("copilot-api.company.ghe.com", request.RequestUri!.Host); return Response(200, """{"data":[{"id":"hidden","policy":{"state":"enabled"}}]}"""); });
        var result = await fixture.Provider.RefreshTokenAsync(Old() with { Metadata = new Dictionary<string, string> { ["enterpriseUrl"] = "company.ghe.com" } });
        Assert.Empty(Ids(result));
    }

    /// <summary>【AI】【目录兼容】无权限字段或无效字段兼容旧凭据，空数组限制全部聊天，大小写严格匹配。</summary><param name="json">字段 JSON；空引用表示缺省。</param><param name="expected">预期聊天 ID。</param>
    [Theory]
    [InlineData(null, "chat,Chat")]
    [InlineData("null", "chat,Chat")]
    [InlineData("\"chat\"", "chat,Chat")]
    [InlineData("[\"chat\",42]", "chat,Chat")]
    [InlineData("[]", "")]
    [InlineData("[\"chat\"]", "chat")]
    public void ModelFilter_RespectsPermissionArray(string? json, string expected)
    {
        var properties = Properties(json);
        var result = GitHubCopilotModelAccess.Filter([Model("chat"), Model("Chat"), Model("picture") with { Type = ModelTypes.Image }], properties);
        Assert.Equal(expected, string.Join(",", result.Where(model => model.Type != ModelTypes.Image).Select(model => model.Id)));
        Assert.Contains(result, model => model.Id == "picture");
    }

    /// <summary>【AI】【SDK 过滤接线】内置 Copilot 的同步、异步及全部能力目录都读取保存的 OAuth 权限数组。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task BuiltInModels_UsesStoredCopilotPermissions()
    {
        var credentials = new InMemoryProviderCredentialStore();
        var configuration = new ModelConfigurationStore([]);
        var resolver = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: configuration);
        var models = BuiltInProviders.CreateBuiltInModels(configuration, resolver, credentialStore: credentials);
        var first = models.GetModels("github-copilot").First();
        using var document = JsonDocument.Parse(new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonValue.Create(first.Id)).ToJsonString());
        await credentials.ModifyAsync("github-copilot", _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.OAuth(new("refresh", "access", DateTimeOffset.MaxValue)
        { Properties = new Dictionary<string, JsonElement> { ["availableModelIds"] = document.RootElement.Clone() } })));
        Assert.Equal(first.Id, Assert.Single(models.GetAvailable("github-copilot")).Id);
        Assert.Equal(first.Id, Assert.Single(await models.GetAvailableAsync("github-copilot")).Id);
        Assert.Equal(first.Id, Assert.Single(await models.GetAllAvailableAsync("github-copilot")).Id);
    }

    /// <summary>【AI】【测试权限】构造可选原生字段。</summary><param name="json">数组或无效 JSON。</param><returns>字段字典。</returns>
    private static IReadOnlyDictionary<string, JsonElement>? Properties(string? json)
    {
        if (json is null) return null; using var document = JsonDocument.Parse(json); return new Dictionary<string, JsonElement> { ["availableModelIds"] = document.RootElement.Clone() };
    }
    /// <summary>【AI】【测试模型】生成聊天条目。</summary><param name="id">标识。</param><returns>模型。</returns>
    private static Model Model(string id) => new() { Id = id, Name = id, Provider = "github-copilot", Api = "fixture" };
    /// <summary>【AI】【测试旧凭据】使用合成访问值。</summary><returns>凭据。</returns>
    private static OAuthCredentials Old() => new() { Access = "old", Refresh = "github-access", ExpiresAt = DateTimeOffset.UnixEpoch };
    /// <summary>【AI】【测试权限读取】读取保存数组。</summary><param name="value">凭据。</param><returns>ID 列表。</returns>
    private static string[] Ids(OAuthCredentials value) => value.Properties["availableModelIds"].EnumerateArray().Select(id => id.GetString()!).ToArray();
    /// <summary>【AI】【测试响应】创建模拟状态和正文。</summary><param name="status">状态码。</param><param name="body">正文。</param><returns>响应。</returns>
    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body) };

    /// <summary>【AI】【测试流程】自动处理设备和令牌端点，账户与 policy 委托给场景。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;
        private double _now = 1000000;
        public List<double> Delays { get; } = [];
        public Callbacks Callbacks { get; } = new();
        public GitHubCopilotOAuthProvider Provider { get; }
        /// <summary>【AI】【测试创建】隔离 HTTP、时间与可启用模型。</summary><param name="catalog">目录或 policy 响应。</param><param name="known">已知模型。</param>
        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> catalog, string[]? known = null)
        {
            _client = new(new Handler(request => request.RequestUri!.AbsolutePath switch
            { "/login/device/code" => Response(200, Device), "/login/oauth/access_token" => Response(200, "{\"access_token\":\"github-access\"}"),
                "/copilot_internal/v2/token" => Response(200, Token), _ => catalog(request) }));
            Provider = new(_client, new OAuthFlowClock { NowMilliseconds = () => _now, SleepAsync = (delay, token) =>
            { token.ThrowIfCancellationRequested(); Delays.Add(delay); _now += delay; return Task.CompletedTask; } }, (known ?? []).ToHashSet(StringComparer.Ordinal));
        }
        /// <summary>【AI】【测试释放】释放模拟客户端。</summary>
        public void Dispose() => _client.Dispose();
    }
    /// <summary>【AI】【测试交互】不打开浏览器，记录启用进度。</summary>
    private sealed class Callbacks : IOAuthLoginCallbacks
    {
        public List<string> Progress { get; } = [];
        /// <inheritdoc />
        public void OnAuth(string url, string? instructions = null) { }
        /// <inheritdoc />
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => Task.FromResult("");
        /// <inheritdoc />
        public void OnDeviceCode(OAuthDeviceCodeNotification notification) { }
        /// <inheritdoc />
        public void OnProgress(string message) => Progress.Add(message);
        /// <inheritdoc />
        public Task<string>? OnManualCodeInputAsync() => null;
    }
    /// <summary>【AI】【测试 HTTP】按场景返回响应。</summary><param name="send">响应函数。</param>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
