// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

/// <summary>【AI】【请求路由】验证会话亲和与请求优先级在真实 HTTP 组装链路中的行为。</summary>
public sealed class ProviderSessionAffinityTests
{
    /// <summary>【AI】【亲和矩阵】列出协议格式、默认检测以及开关优先级。</summary>
    /// <returns>协议、场景与预期请求头名称。</returns>
    public static TheoryData<string, string, string> HeaderCases()
    {
        var cases = new TheoryData<string, string, string>();
        foreach (var api in new[] { "openai-completions", "compatible", "openai-responses", "anthropic-messages" })
        {
            var responses = api == "openai-responses";
            var anthropic = api == "anthropic-messages";
            var openAi = anthropic ? "x-session-affinity" : responses
                ? "session_id,x-client-request-id" : "session_id,x-client-request-id,x-session-affinity";
            var noSession = anthropic ? "x-session-affinity" : responses
                ? "x-client-request-id" : "x-client-request-id,x-session-affinity";
            cases.Add(api, "openai", openAi);
            cases.Add(api, "openai-nosession", noSession);
            cases.Add(api, "openrouter", "x-session-id");
            cases.Add(api, "default", responses ? openAi : "");
            cases.Add(api, "provider-detection", "x-session-id");
            cases.Add(api, "url-detection", "x-session-id");
            cases.Add(api, "disabled", responses ? "x-session-id" : "");
            cases.Add(api, "explicit-format", openAi);
            cases.Add(api, "no-session", "");
        }
        return cases;
    }

    /// <summary>【AI】【会话报文】检查协议默认值、显式格式、发送开关与缺少会话标识。</summary>
    /// <param name="api">实际实现。</param><param name="scenario">配置场景。</param><param name="expected">应存在的头名称。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [MemberData(nameof(HeaderCases))]
    public async Task RequestsUseProtocolAffinityHeaders(string api, string scenario, string expected)
    {
        using var handler = CreateHandler();
        using var client = new HttpClient(handler);
        var format = scenario is "openai" or "openai-nosession" or "openrouter" ? scenario
            : scenario == "explicit-format" ? "openai" : null;
        var model = CreateModel(api) with
        {
            Provider = scenario is "provider-detection" or "disabled" or "explicit-format" ? "openrouter" : "local",
            BaseUrl = scenario == "url-detection" ? "https://openrouter.ai/api/v1" : "https://example.invalid/v1",
            Compat = new()
            {
                SessionAffinityFormat = format,
                SendSessionAffinityHeaders = scenario == "disabled" ? false : format is not null ? true : null
            }
        };
        await OpenAiResponsesProviderTests.CollectAsync(CreateProvider(api, client).Stream(model,
            new() { Messages = [new UserMessage("hi")] },
            new() { ApiKey = "synthetic-test", SessionId = scenario == "no-session" ? "" : "route-123", MaxRetries = 0 }));
        var request = Assert.Single(handler.Requests);
        var names = expected.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in new[] { "x-session-id", "session_id", "x-client-request-id", "x-session-affinity" })
        {
            Assert.Equal(names.Contains(name), request.Headers.Contains(name));
            if (names.Contains(name)) Assert.Equal("route-123", Assert.Single(request.Headers.GetValues(name)));
        }
    }

    /// <summary>【AI】【请求覆盖】请求头最后覆盖或删除模型及自动亲和头，转换回调观察最终值。</summary>
    /// <param name="api">实际实现。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("openai-completions")]
    [InlineData("compatible")]
    [InlineData("openai-responses")]
    [InlineData("anthropic-messages")]
    public async Task RequestHeadersOverrideAffinityDefaults(string api)
    {
        using var handler = CreateHandler();
        using var client = new HttpClient(handler);
        var model = CreateModel(api) with
        {
            Compat = new() { SessionAffinityFormat = "openrouter", SendSessionAffinityHeaders = true },
            Headers = new Dictionary<string, string> { ["x-session-id"] = "model", ["x-client-request-id"] = "model" }
        };
        var observed = false;
        await OpenAiResponsesProviderTests.CollectAsync(CreateProvider(api, client).StreamSimple(model,
            new() { Messages = [new UserMessage("hi")] },
            new()
            {
                ApiKey = "synthetic-test", SessionId = "automatic", MaxRetries = 0,
                Headers = new Dictionary<string, string> { ["X-Session-Id"] = "request", ["X-Client-Request-Id"] = "" },
                TransformHeaders = (headers, _) =>
                {
                    Assert.Equal("request", headers["x-session-id"]);
                    Assert.DoesNotContain(headers.Keys, name => name.Equals("x-client-request-id", StringComparison.OrdinalIgnoreCase));
                    observed = true;
                    return ValueTask.FromResult<IDictionary<string, string?>?>(headers);
                }
            }));
        Assert.True(observed);
        Assert.Equal("request", Assert.Single(Assert.Single(handler.Requests).Headers.GetValues("x-session-id")));
    }

    /// <summary>【AI】【优先级报文】保留零、负数和分数；缺省不发送，请求级采样参数可覆盖。</summary>
    /// <param name="compatible">是否使用兼容别名实现。</param><param name="priority">模型优先级。</param>
    /// <param name="samplingOverride">是否用请求采样参数覆盖。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, 0d, false)]
    [InlineData(false, -2d, false)]
    [InlineData(false, 0.5d, false)]
    [InlineData(false, 1d, true)]
    [InlineData(true, null, false)]
    [InlineData(true, 0d, false)]
    [InlineData(true, -2d, false)]
    [InlineData(true, 0.5d, false)]
    [InlineData(true, 1d, true)]
    public async Task CompletionsPassesVllmPriority(bool compatible, double? priority, bool samplingOverride)
    {
        using var handler = CreateHandler();
        using var client = new HttpClient(handler);
        var api = compatible ? "compatible" : "openai-completions";
        var model = CreateModel(api) with { Compat = new() { VllmPriority = priority } };
        await OpenAiResponsesProviderTests.CollectAsync(CreateProvider(api, client).StreamSimple(model,
            new() { Messages = [new UserMessage("hi")] },
            new()
            {
                ApiKey = "synthetic-test", MaxRetries = 0,
                SamplingParams = samplingOverride ? new Dictionary<string, object> { ["priority"] = 9 } : null
            }));
        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        Assert.Equal(priority.HasValue || samplingOverride, body.RootElement.TryGetProperty("priority", out var value));
        if (priority.HasValue || samplingOverride) Assert.Equal(samplingOverride ? 9 : priority!.Value, value.GetDouble());
    }

    /// <summary>【AI】【提供方夹具】使用相同内存 HTTP 客户端实例化各协议实现。</summary>
    /// <param name="api">实现名称。</param><param name="client">测试客户端。</param><returns>提供方。</returns>
    private static IStreamProvider CreateProvider(string api, HttpClient client) => api switch
    {
        "openai-completions" => new OpenAiProvider(client),
        "openai-responses" => new OpenAiResponsesProvider(client),
        "anthropic-messages" => new AnthropicProvider(client),
        _ => new OpenAiCompatibleProvider("compatible", "https://example.invalid/v1", httpClient: client)
    };

    /// <summary>【AI】【模型夹具】创建不依赖外部认证或目录的模型。</summary>
    /// <param name="api">协议名称。</param><returns>模型。</returns>
    private static Model CreateModel(string api) => new()
    {
        Id = "test-model", Name = "Test model", Provider = "local", Api = api,
        BaseUrl = "https://example.invalid/v1", MaxOutputTokens = 1024
    };

    /// <summary>【AI】【传输夹具】捕获请求后立即结束，不产生外部调用。</summary>
    /// <returns>内存传输层。</returns>
    private static OpenAiResponsesProviderTests.StubHandler CreateHandler() => new(_ => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent("stop after payload")
    });
}
