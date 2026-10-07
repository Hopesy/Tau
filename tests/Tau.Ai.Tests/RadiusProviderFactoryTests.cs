// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【Radius】【提供方工厂测试】验证公共目录边界、自定义标识、网关认证和 SDK 登录刷新链路。</summary>
public sealed class RadiusProviderFactoryTests
{
    private const string Catalog = """{"baseUrl":"https://stream.invalid/api","models":[{"id":"remote","name":"Remote","reasoning":false,"input":["text"],"cost":{},"contextWindow":1000,"maxTokens":100}]}""";
    private const string Token = """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"scope":"gateway offline_access"}""";

    /// <summary>【Radius】【公共基线】规范化后的默认网关拥有基线，并将模型归属改为指定标识。</summary>
    /// <param name="gateway">网关写法。</param>
    [Theory]
    [InlineData(null)]
    [InlineData("radius.pi.dev/")]
    [InlineData(" https://radius.pi.dev/// ")]
    public void PublicGateway_UsesBaselineWithCustomProviderId(string? gateway)
    {
        var provider = BuiltInProviders.CreateRadiusProvider("team", "Team Gateway", gateway);
        Assert.Equal("team", provider.Id); Assert.Equal("Team Gateway", provider.Name); Assert.True(provider.IsDynamic);
        Assert.NotEmpty(provider.GetModels()); Assert.All(provider.GetModels(), model => Assert.Equal("team", model.Provider));
        Assert.Equal("Radius API key", provider.Auth!.ApiKey!.Name); Assert.Equal("Team Gateway", provider.Auth.OAuth!.Name);
    }

    /// <summary>【Radius】【自定义网关】非默认网关不注入公共模型，包括不同端口或路径。</summary><param name="gateway">网关。</param>
    [Theory]
    [InlineData("https://team.invalid")]
    [InlineData("https://radius.pi.dev/path")]
    [InlineData("https://radius.pi.dev:8443")]
    public void CustomGateway_StartsWithEmptyCatalog(string gateway) => Assert.Empty(BuiltInProviders.CreateRadiusProvider(gateway: gateway).GetModels());

    /// <summary>【Radius】【密钥来源】已存密钥优先，环境变量仅在其不存在时解析，并保留来源元数据。</summary>
    /// <param name="stored">保存的密钥。</param><param name="environment">环境变量。</param><param name="expected">预期密钥。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("stored", "environment", "stored")]
    [InlineData("", "environment", "environment")]
    [InlineData(null, "", null)]
    [InlineData(" ", "environment", " ")]
    public async Task ApiKeyAuth_UsesStoredKeyThenEnvironment(string? stored, string environment, string? expected)
    {
        var provider = BuiltInProviders.CreateRadiusProvider("team");
        var savedEnv = new Dictionary<string, string> { ["SAVED"] = "yes" };
        var result = await provider.Auth!.ApiKey!.ResolveAsync(new("team", new(stored, savedEnv), new Dictionary<string, string> { ["RADIUS_API_KEY"] = environment }, default));
        Assert.Equal(expected, result?.ApiKey);
        if (expected is not null)
        {
            Assert.Equal(string.IsNullOrEmpty(stored) ? "RADIUS_API_KEY" : "stored credential", result!.Source);
            Assert.Equal(string.IsNullOrEmpty(stored) ? null : savedEnv, result.Env);
        }
    }

    /// <summary>【Radius】【秘密输入】SDK 密钥登录保留秘密提示并把合成密钥保存到自定义提供方。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ApiKeyLogin_PromptsAndSavesUnderCustomId()
    {
        var credentials = new InMemoryProviderCredentialStore();
        var models = new Models([BuiltInProviders.CreateRadiusProvider("team")], credentialStore: credentials);
        var interaction = new Interaction("fixture-key");
        await models.LoginAsync("team", "api_key", interaction);
        Assert.Equal("secret", interaction.Prompt!.Type); Assert.Equal("Enter Radius API key", interaction.Prompt.Message);
        Assert.Equal("fixture-key", Assert.IsType<ProviderCredential.ApiKey>(await credentials.ReadAsync("team")).Value.Key);
        Assert.Null(await credentials.ReadAsync("radius"));
    }

    /// <summary>【Radius】【取消解析】已取消的认证和登录不会继续交互或读取密钥。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task CanceledAuth_StopsBeforeInteraction()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        var auth = BuiltInProviders.CreateRadiusProvider().Auth!.ApiKey!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.ResolveAsync(new("radius", new("fixture-key"), null, source.Token)));
        var interaction = new Interaction("unused", source.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.LoginAsync!(interaction)); Assert.Null(interaction.Prompt);
    }

    /// <summary>【Radius】【设备登录链路】自定义网关登录与目录刷新共用同一客户端和 origin，凭据保留授权 scope。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task DeviceLogin_ThenRefresh_UsesCustomGatewayAndName()
    {
        var paths = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("team.invalid", request.RequestUri!.Host); paths.Add(request.RequestUri.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/v1/oauth/device" => Response("""{"device_code":"device","user_code":"CODE","verification_uri":"https://team.invalid/verify","expires_in":120,"interval":1}"""),
                "/v1/oauth/token" => Response(Token),
                "/v1/config" => AuthorizedCatalog(request),
                _ => throw new InvalidOperationException("Unexpected request")
            };
        }));
        var credentials = new InMemoryProviderCredentialStore(); var store = new InMemoryModelsStore();
        var models = new Models([BuiltInProviders.CreateRadiusProvider("team", "Team Gateway", "team.invalid/subpath", client)], credentialStore: credentials, modelsStore: store);
        var interaction = new Interaction("device-code");
        var loggedIn = Assert.IsType<ProviderCredential.OAuth>(await models.LoginAsync("team", "oauth", interaction, new OAuthLoginOptions(() => throw new InvalidOperationException("Device identity should remain lazy"))));
        Assert.Equal("Sign in to Team Gateway:", interaction.Prompt!.Message); Assert.Equal("gateway offline_access", loggedIn.Value.Metadata!["scope"]);
        Assert.Contains(interaction.Notifications, notification => notification.Type == "device_code" && notification.UserCode == "CODE");
        Assert.Empty((await models.RefreshAsync()).Errors);
        var model = Assert.Single(models.GetModels()); Assert.Equal("team", model.Provider); Assert.Equal("https://stream.invalid/api", model.BaseUrl);
        Assert.Equal(new[] { "/v1/oauth/device", "/v1/oauth/token", "/v1/config" }, paths);
        Assert.Equal("team", Assert.Single((await store.ReadAsync("team"))!.Models).Provider); Assert.Null(await store.ReadAsync("radius"));
    }

    /// <summary>【Radius】【过期令牌】目录刷新先在自定义网关轮换 OAuth，再用新 access 加载目录。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ExpiredOAuth_RefreshesAtSameGatewayBeforeCatalog()
    {
        var paths = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("team.invalid", request.RequestUri!.Host); paths.Add(request.RequestUri.AbsolutePath);
            return request.RequestUri.AbsolutePath == "/v1/oauth/token" ? Response(Token) : AuthorizedCatalog(request);
        }));
        var credentials = new InMemoryProviderCredentialStore();
        await credentials.ModifyAsync("team", _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.OAuth(new("old-refresh", "old-access", DateTimeOffset.UnixEpoch))));
        var models = new Models([BuiltInProviders.CreateRadiusProvider("team", gateway: "team.invalid", httpClient: client)], credentialStore: credentials);
        Assert.Empty((await models.RefreshAsync()).Errors);
        Assert.Equal(new[] { "/v1/oauth/token", "/v1/config" }, paths);
        Assert.Equal("new-refresh", Assert.IsType<ProviderCredential.OAuth>(await credentials.ReadAsync("team")).Value.Refresh);
    }

    /// <summary>【Radius】【自定义旧目录】旧 OAuth 中的目录离线导入自定义提供方与其缓存键。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task CustomProvider_ImportsLegacyCatalogOffline()
    {
        using var document = JsonDocument.Parse(Catalog); var credentials = new InMemoryProviderCredentialStore(); var store = new InMemoryModelsStore();
        await credentials.ModifyAsync("team", _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.OAuth(new("refresh", "access", DateTimeOffset.MaxValue)
        { Properties = new Dictionary<string, JsonElement> { ["gatewayConfig"] = document.RootElement.Clone() } })));
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Offline")));
        var models = new Models([BuiltInProviders.CreateRadiusProvider("team", gateway: "team.invalid", httpClient: client)], credentialStore: credentials, modelsStore: store);
        Assert.Empty((await models.RefreshAsync(allowNetwork: false)).Errors);
        Assert.Equal("team", Assert.Single(models.GetModels()).Provider); Assert.NotNull(await store.ReadAsync("team")); Assert.Null(await store.ReadAsync("radius"));
    }

    /// <summary>【Radius】【测试目录请求】核验轮换后的认证头并返回模拟目录。</summary><param name="request">请求。</param><returns>响应。</returns>
    private static HttpResponseMessage AuthorizedCatalog(HttpRequestMessage request)
    {
        Assert.Equal("Bearer new-access", request.Headers.Authorization!.ToString()); return Response(Catalog);
    }

    /// <summary>【Radius】【测试响应】生成成功 JSON。</summary><param name="body">正文。</param><returns>响应。</returns>
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>【Radius】【测试 HTTP】同步生成模拟响应。</summary><param name="send">响应函数。</param>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    /// <summary>【Radius】【测试交互】记录秘密输入、方式选择及设备通知。</summary><param name="answer">合成输入。</param><param name="token">取消信号。</param>
    private sealed class Interaction(string answer, CancellationToken token = default) : AuthInteraction
    {
        public CancellationToken CancellationToken => token;
        public ProviderAuthPrompt? Prompt { get; private set; }
        public List<ProviderAuthNotification> Notifications { get; } = [];
        /// <inheritdoc />
        public Task<string> PromptAsync(ProviderAuthPrompt prompt) { Prompt = prompt; return Task.FromResult(answer); }
        /// <inheritdoc />
        public void Notify(ProviderAuthNotification notification) => Notifications.Add(notification);
        /// <inheritdoc />
        public Task<string> PromptAsync(string prompt) => throw new NotSupportedException();
        /// <inheritdoc />
        public void Notify(string message) { }
    }
}
