// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Cloudflare;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【分类网关回归】覆盖 Cloudflare 信封、认证和 Vercel 的统一入口。</summary>
[Collection("ProcessEnvironment")]
public sealed class CloudflareClassifierTests
{
    private const string ProviderId = "cloudflare-workers-ai";
    private const string AccountVariable = "CLOUDFLARE_ACCOUNT_ID";
    private const string Output = """
        {"answers":{"safe":{"type":"noul","noul":0.9}},"usage":{"input_tokens":100,"output_tokens":20}}
        """;
    private static readonly ClassifierModel Model = new()
    {
        Id = "typesafe/jev", Name = "Jev", Provider = ProviderId, Api = "cloudflare-workers-ai-system-one",
        BaseUrl = "https://unit.test/accounts/{CLOUDFLARE_ACCOUNT_ID}/ai/", ContextWindow = 32_000, Cost = new ModelCost(1, 2)
    };

    /// <summary>两种响应信封均映射成公共答案；请求体和回调使用已解析账户地址。</summary>
    /// <param name="nested">是否使用第三方任务信封。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Classify_MapsCloudflareEnvelopes(bool nested)
    {
        using var handler = new Handler(async (request, _) =>
        {
            Assert.Equal("https://unit.test/accounts/account/ai/run", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer key", request.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(Model.Id, body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.TryGetProperty("state", out var ignoredState));
            var input = body.RootElement.GetProperty("input");
            Assert.Equal("测试", input.GetProperty("state").GetProperty("text").GetString());
            Assert.Equal("noul", input.GetProperty("questions").GetProperty("safe").GetProperty("type").GetString());
            return Response(nested ? "{\"success\":true,\"result\":{\"state\":\"Completed\",\"result\":" + Output + "}}" : Envelope(Output));
        });
        using var client = new HttpClient(handler);
        var payloadCalls = 0;
        var responseCalls = 0;
        var result = await new CloudflareWorkersAiSystemOneProvider(client).ClassifyAsync(Model, Context(), new ClassifierOptions
        {
            ApiKey = "key", Env = Environment("account"),
            OnPayload = (payload, actual) =>
            {
                Assert.Equal("https://unit.test/accounts/account/ai/", actual.BaseUrl);
                Assert.True(payload.TryGetProperty("input", out _));
                payloadCalls++;
                return ValueTask.FromResult<JsonElement?>(null);
            },
            OnResponse = (response, actual) =>
            {
                Assert.Equal(200, response.Status);
                Assert.Equal("https://unit.test/accounts/account/ai/", actual.BaseUrl);
                responseCalls++;
                return ValueTask.CompletedTask;
            }
        });
        Assert.True(result.StopReason == ClassifierStopReason.Stop, result.ErrorMessage);
        Assert.Equal(0.9, Assert.IsType<ClassifierBoolAnswer>(Assert.Single(result.Answers).Value).Probability);
        Assert.Equal(120, result.Usage!.Value.TotalTokens);
        Assert.Equal(0.00014m, result.Usage.Value.Cost!.Value.Total);
        Assert.Equal(1, payloadCalls);
        Assert.Equal(1, responseCalls);
        Assert.Contains("{CLOUDFLARE_ACCOUNT_ID}", Model.BaseUrl!);
    }

    /// <summary>Cloudflare 的业务失败和未完成状态不触发 HTTP 重试。</summary>
    /// <param name="body">响应内容。</param>
    /// <param name="error">预期错误片段。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("null", "unexpected response")]
    [InlineData("[]", "unexpected response")]
    [InlineData("{}", "unexpected response")]
    [InlineData("{\"success\":false,\"errors\":[{\"message\":\"first\"},7,{\"message\":\"second\"}]}", "first; second")]
    [InlineData("{\"success\":false,\"errors\":[{\"code\":1}]}", "request failed")]
    [InlineData("{\"result\":null}", "unexpected response")]
    [InlineData("{\"result\":{\"state\":\"Queued\"}}", "state: Queued")]
    [InlineData("{\"result\":{}}", "state: undefined")]
    [InlineData("{\"result\":{\"state\":\"Completed\",\"result\":null}}", "unexpected response")]
    public async Task Classify_RejectsInvalidEnvelopes(string body, string error)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(body)));
        using var client = new HttpClient(handler);
        var result = await new CloudflareWorkersAiSystemOneProvider(client).ClassifyAsync(Model, Context(), new ClassifierOptions { ApiKey = "key", Env = Environment("account") });
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Contains("Cloudflare Workers AI", result.ErrorMessage!);
        Assert.Contains(error, result.ErrorMessage!);
        Assert.Empty(result.Answers);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>嵌套输出损坏时仍保留已发生的计费用量。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Classify_PreservesBilledUsageForMalformedAnswers()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Envelope("{\"answers\":{},\"usage\":{\"input_tokens\":100}}"))));
        using var client = new HttpClient(handler);
        var result = await new CloudflareWorkersAiSystemOneProvider(client).ClassifyAsync(Model, Context(), new ClassifierOptions { ApiKey = "key", Env = Environment("account") });
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Equal(100, result.Usage!.Value.InputTokens);
        Assert.Equal(0.0001m, result.Usage.Value.Cost!.Value.Total);
    }

    /// <summary>统一入口调用目录内的三种 Cloudflare 模型，使用账户及环境 key。</summary>
    /// <param name="id">分类模型标识。</param>
    /// <param name="contextWindow">目录上下文长度。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("@cf/cloudflare/clef", 65536)]
    [InlineData("@cf/cloudflare/clef-flash", 65536)]
    [InlineData("typesafe/jev", 32000)]
    public async Task BuiltInModels_RoutesCloudflareClassifiers(string id, int contextWindow)
    {
        using var handler = new Handler(async (request, _) =>
        {
            Assert.Equal("https://api.cloudflare.com/client/v4/accounts/account/ai/run", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer env-key", request.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(id, body.RootElement.GetProperty("model").GetString());
            return Response(Envelope(Output));
        });
        using var client = new HttpClient(handler);
        var models = BuiltIns(client);
        var model = Assert.IsType<ClassifierModel>(models.GetModelOfType(ModelTypes.Classifier, ProviderId, id));
        Assert.Equal(contextWindow, model.ContextWindow);
        Assert.DoesNotContain(models.GetModels(ProviderId), item => item.Id == id);
        var env = Environment("account");
        env["CLOUDFLARE_API_KEY"] = "env-key";
        var result = await models.ClassifyAsync(model, Context(), new ClassifierOptions { Env = env });
        Assert.True(result.StopReason == ClassifierStopReason.Stop, result.ErrorMessage);
        Assert.Equal(ProviderId, result.Provider);
    }

    /// <summary>Vercel 分类走 System One 路径，并复用环境认证及 HTTP 重试。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task BuiltInModels_RoutesVercelClassifierAndRetries()
    {
        var attempts = 0;
        var payloadCalls = 0;
        var responseCalls = 0;
        using var handler = new Handler(async (request, _) =>
        {
            Assert.Equal("https://ai-gateway.vercel.sh/typesafe/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer vercel-key", request.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("typesafe-ai/jev", body.RootElement.GetProperty("model").GetString());
            Assert.True(body.RootElement.TryGetProperty("questions", out var ignoredQuestions));
            return ++attempts == 1 ? Response("busy", HttpStatusCode.TooManyRequests) : Response(Output);
        });
        using var client = new HttpClient(handler);
        var models = BuiltIns(client);
        var model = Assert.IsType<ClassifierModel>(models.GetModelOfType(ModelTypes.Classifier, "vercel-ai-gateway", "typesafe-ai/jev"));
        Assert.Equal(0.042m, model.Cost!.Value.InputPerMillion);
        var result = await models.ClassifyAsync(model, Context(), new ClassifierOptions
        {
            Env = new Dictionary<string, string> { ["AI_GATEWAY_API_KEY"] = "vercel-key" },
            OnPayload = (_, _) => { payloadCalls++; return ValueTask.FromResult<JsonElement?>(null); },
            OnResponse = (_, _) => { responseCalls++; return ValueTask.CompletedTask; }
        });
        Assert.True(result.StopReason == ClassifierStopReason.Stop, result.ErrorMessage);
        Assert.Equal(2, attempts);
        Assert.Equal(1, payloadCalls);
        Assert.Equal(1, responseCalls);
    }

    /// <summary>【AI】【Cloudflare 请求覆盖】普通环境回退保留凭据账户，显式请求环境先覆盖凭据字段。</summary>
    /// <param name="storedAccount">存储账户，为 null 时使用请求环境。</param>
    /// <param name="explicitKey">本次显式 key，为 null 时使用存储凭据。</param>
    /// <param name="overrideAccount">是否传递本次请求账户覆盖。</param>
    /// <param name="expectedAccount">预期用于请求的账户。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("stored-account", null, false, "stored-account")]
    [InlineData("stored-account", null, true, "request-account")]
    [InlineData(null, null, true, "request-account")]
    [InlineData("stored-account", "explicit-key", true, "request-account")]
    public async Task Authentication_PreservesCredentialAccountPrecedence(string? storedAccount, string? explicitKey, bool overrideAccount, string expectedAccount)
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set(AccountVariable, "ambient-account");
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal($"https://api.cloudflare.com/client/v4/accounts/{expectedAccount}/ai/run", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer " + (explicitKey ?? "stored-key"), request.Headers.Authorization!.ToString());
            return Task.FromResult(Response(Envelope(Output)));
        });
        using var client = new HttpClient(handler);
        var store = new InMemoryProviderCredentialStore();
        await store.ModifyAsync(ProviderId, _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.ApiKey(new("stored-key", storedAccount is null ? null : Environment(storedAccount)))));
        var models = new Models([BuiltIns(client).GetProvider(ProviderId)!], credentialStore: store);
        var model = models.GetModelOfType(ModelTypes.Classifier, ProviderId, "typesafe/jev")!;
        var env = overrideAccount ? Environment("request-account") : new Dictionary<string, string>();
        Assert.True((await models.CheckAuthAsync(ProviderId, explicitKey, env))!.IsConfigured);
        var result = await models.ClassifyAsync(model, Context(), new ClassifierOptions { ApiKey = explicitKey, Env = env });
        Assert.True(result.StopReason == ClassifierStopReason.Stop, result.ErrorMessage);
        if (overrideAccount) Assert.Equal("request-account", env[AccountVariable]);
        else Assert.Empty(env);
    }

    /// <summary>账户缺失或显式为空时，检查状态与实际调用均拒绝发送请求。</summary>
    /// <param name="stored">是否使用保存了空账户的凭据。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authentication_RejectsMissingAccount(bool stored)
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set(AccountVariable, "fallback-account");
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = new HttpClient(handler);
        var store = new InMemoryProviderCredentialStore();
        if (stored) await store.ModifyAsync(ProviderId, _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.ApiKey(new("key", Environment("")))));
        var models = new Models([BuiltIns(client).GetProvider(ProviderId)!], credentialStore: store);
        var env = stored ? new Dictionary<string, string>() : Environment("");
        var explicitKey = stored ? null : "key";
        Assert.False((await models.CheckAuthAsync(ProviderId, explicitKey, env))!.IsConfigured);
        var result = await models.ClassifyAsync(models.GetModelOfType(ModelTypes.Classifier, ProviderId, "typesafe/jev")!, Context(), new ClassifierOptions { ApiKey = explicitKey, Env = env });
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>显式 key 缺少账户字段时，可使用进程环境中的账户。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Authentication_FallsBackToProcessAccount()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set(AccountVariable, "process-account");
        var models = BuiltIns();
        var auth = await models.ResolveAuthAsync(Model, "explicit-key");
        Assert.Equal("explicit-key", auth!.ApiKey);
        Assert.Equal("process-account", auth.Env![AccountVariable]);
        Assert.True((await models.CheckAuthAsync(ProviderId, "explicit-key"))!.IsConfigured);
    }

    /// <summary>登录收集 key 和账户，保存后同步与异步可用查询都能发现分类模型。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Authentication_LoginStoresBothFieldsAndEnablesAvailability()
    {
        var store = new InMemoryProviderCredentialStore();
        await store.ModifyAsync(ProviderId, _ => Task.FromResult<ProviderCredential?>(new ProviderCredential.ApiKey(new("", Environment("")))));
        var models = new Models([BuiltIns().GetProvider(ProviderId)!], credentialStore: store);
        Assert.Empty(models.GetAvailableOfType(ModelTypes.Classifier, ProviderId));
        Assert.Empty(await models.GetAvailableOfTypeAsync(ModelTypes.Classifier, ProviderId));
        var interaction = new LoginInteraction();
        var credential = Assert.IsType<ProviderCredential.ApiKey>(await models.LoginAsync(ProviderId, "api_key", interaction));
        Assert.Equal("login-key", credential.Value.Key);
        Assert.Equal("login-account", credential.Value.Env![AccountVariable]);
        Assert.Equal(credential, await store.ReadAsync(ProviderId));
        Assert.Equal(new[] { "Enter Cloudflare API key", "Enter Cloudflare account ID" }, interaction.Prompts);
        Assert.Equal(3, models.GetAvailableOfType(ModelTypes.Classifier, ProviderId).Count);
        Assert.Equal(3, (await models.GetAvailableOfTypeAsync(ModelTypes.Classifier, ProviderId)).Count);
    }

    /// <summary>兼容旧 auth.json 中的 key 与账户，缺失账户字段时允许环境补全。</summary>
    /// <param name="hasAccount">旧凭据是否存有账户。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authentication_LoadsLegacyCredentials(bool hasAccount)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-cloudflare-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "auth.json");
            await File.WriteAllTextAsync(path, hasAccount
                ? "{\"cloudflare-workers-ai\":{\"type\":\"api_key\",\"key\":\"legacy-key\",\"env\":{\"CLOUDFLARE_ACCOUNT_ID\":\"legacy-account\"}}}"
                : "{\"cloudflare-workers-ai\":{\"type\":\"api_key\",\"key\":\"legacy-key\"}}");
            var configuration = new ModelConfigurationStore([]);
            var resolver = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([path]), configurationStore: configuration);
            var models = BuiltInProviders.CreateBuiltInModels(configuration, resolver);
            var auth = await models.ResolveAuthAsync(Model, env: Environment("fallback-account"));
            Assert.Equal("legacy-key", auth!.ApiKey);
            Assert.Equal(hasAccount ? "legacy-account" : "fallback-account", auth.Env![AccountVariable]);
            Assert.True((await models.CheckAuthAsync(ProviderId, env: Environment("fallback-account")))!.IsConfigured);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Cloudflare 的 HTTP 重试复用转换后的信封，并保持最终回调一次。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Classify_RetriesTransportWithSinglePayloadTransformation()
    {
        var attempts = 0;
        var payloadCalls = 0;
        var responseCalls = 0;
        using var handler = new Handler(async (request, _) =>
        {
            Assert.Equal("{\"replacement\":true}", await request.Content!.ReadAsStringAsync());
            Assert.Equal("Bearer override", request.Headers.Authorization!.ToString());
            return ++attempts == 1 ? Response("busy", HttpStatusCode.ServiceUnavailable) : Response(Envelope(Output));
        });
        using var client = new HttpClient(handler);
        var result = await new CloudflareWorkersAiSystemOneProvider(client).ClassifyAsync(Model, Context(), new ClassifierOptions
        {
            ApiKey = "key", Env = Environment("account"), Headers = new Dictionary<string, string> { ["authorization"] = "Bearer override" },
            OnPayload = (payload, _) =>
            {
                Assert.True(payload.TryGetProperty("input", out var ignoredInput));
                payloadCalls++;
                using var replacement = JsonDocument.Parse("{\"replacement\":true}");
                return ValueTask.FromResult<JsonElement?>(replacement.RootElement.Clone());
            },
            OnResponse = (_, _) => { responseCalls++; return ValueTask.CompletedTask; }
        });
        Assert.True(result.StopReason == ClassifierStopReason.Stop, result.ErrorMessage);
        Assert.Equal(2, attempts);
        Assert.Equal(1, payloadCalls);
        Assert.Equal(1, responseCalls);
    }

    /// <summary>底层自定义地址不强制要求账户，带占位符的地址必须提前解析。</summary>
    /// <param name="customAddress">是否提供完整自定义地址。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Classify_ResolvesOnlyPresentPlaceholders(bool customAddress)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://custom.test/v1/run", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response(Envelope(Output)));
        });
        using var client = new HttpClient(handler);
        var model = customAddress ? Model with { BaseUrl = "https://custom.test/v1///" } : Model;
        var result = await new CloudflareWorkersAiSystemOneProvider(client).ClassifyAsync(model, Context(), new ClassifierOptions { ApiKey = "key" });
        Assert.Equal(customAddress ? ClassifierStopReason.Stop : ClassifierStopReason.Error, result.StopReason);
        Assert.Equal(customAddress ? 1 : 0, handler.Calls);
        if (!customAddress) Assert.Contains(AccountVariable, result.ErrorMessage!);
    }

    /// <summary>创建不读取用户磁盘配置或凭据的内置集合。</summary>
    /// <param name="client">可选模拟 HTTP 客户端。</param>
    /// <returns>隔离的模型集合。</returns>
    private static Models BuiltIns(HttpClient? client = null)
    {
        var configuration = new ModelConfigurationStore([]);
        return BuiltInProviders.CreateBuiltInModels(configuration, new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: configuration), client);
    }

    /// <summary>构造单个布尔问题。</summary>
    /// <returns>持有独立 JSON 内存的上下文。</returns>
    private static ClassifierContext Context()
    {
        using var state = JsonDocument.Parse("{\"text\":\"测试\"}");
        return new(state.RootElement.Clone(), new Dictionary<string, ClassifierQuestion> { ["safe"] = new ClassifierBoolQuestion("check", new("safe", "unsafe")) });
    }

    /// <summary>构造请求环境账户覆盖。</summary>
    /// <param name="account">账户标识。</param>
    /// <returns>独立环境字典。</returns>
    private static Dictionary<string, string> Environment(string account) => new() { [AccountVariable] = account };

    /// <summary>包装 Cloudflare 直接输出。</summary>
    /// <param name="output">模型输出。</param>
    /// <returns>响应 JSON。</returns>
    private static string Envelope(string output) => "{\"success\":true,\"result\":" + output + "}";

    /// <summary>构造可立即重试的模拟 HTTP 响应。</summary>
    /// <param name="body">响应内容。</param>
    /// <param name="status">HTTP 状态。</param>
    /// <returns>响应消息。</returns>
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        response.Headers.TryAddWithoutValidation("retry-after-ms", "0");
        return response;
    }

    private sealed class LoginInteraction : AuthInteraction
    {
        public CancellationToken CancellationToken => CancellationToken.None;
        public List<string> Prompts { get; } = [];

        /// <summary>核对 Cloudflare 密钥使用秘密输入，再复用模拟返回值。</summary>
        /// <param name="prompt">类型化输入。</param><returns>模拟输入值。</returns>
        public Task<string> PromptAsync(ProviderAuthPrompt prompt)
        {
            Assert.Equal("secret", prompt.Type);
            return PromptAsync(prompt.Message);
        }

        /// <summary>按登录提示返回模拟 key 和账户。</summary>
        /// <param name="prompt">登录提示。</param>
        /// <returns>对应的模拟输入。</returns>
        public Task<string> PromptAsync(string prompt)
        {
            Prompts.Add(prompt);
            return Task.FromResult(Prompts.Count == 1 ? "login-key" : "login-account");
        }

        /// <summary>拒绝此登录流程不应产生的额外通知。</summary>
        /// <param name="message">通知内容。</param>
        public void Notify(string message) => throw new InvalidOperationException(message);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        /// <summary>计数并转发模拟请求。</summary>
        /// <param name="request">请求消息。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>模拟响应。</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
