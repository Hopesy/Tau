// 作者：xxx
using System.Net;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.Bedrock;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

/// <summary>【AI】【直接调用】检查绕过统一入口后的缓存默认值和 Responses 兼容开关。</summary>
[Collection("BedrockEnvironment")]
public sealed class DirectProviderCacheDefaultsTests
{
    /// <summary>【AI】【缓存矩阵】为四种协议生成普通与简化入口的缓存优先级用例。</summary>
    /// <returns>协议、入口、环境值、显式策略以及预期策略。</returns>
    public static TheoryData<string, bool, string, CacheRetention?, CacheRetention> CacheCases()
    {
        var cases = new TheoryData<string, bool, string, CacheRetention?, CacheRetention>();
        foreach (var api in new[] { "openai-completions", "openai-responses", "anthropic-messages", "bedrock-converse-stream" })
        foreach (var simple in new[] { false, true })
        {
            cases.Add(api, simple, "", null, CacheRetention.Short);
            cases.Add(api, simple, "long", null, CacheRetention.Long);
            cases.Add(api, simple, "unknown", null, CacheRetention.Short);
            cases.Add(api, simple, "long", CacheRetention.None, CacheRetention.None);
            cases.Add(api, simple, "long", CacheRetention.Short, CacheRetention.Short);
            cases.Add(api, simple, "", CacheRetention.Long, CacheRetention.Long);
        }
        return cases;
    }

    /// <summary>【AI】【缓存报文】通过捕获 HTTP 请求检查默认策略、环境覆盖和调用对象不变性。</summary>
    /// <param name="api">协议名称。</param><param name="simple">是否调用简化入口。</param>
    /// <param name="environment">缓存环境值。</param><param name="explicitRetention">可选显式策略。</param>
    /// <param name="expected">应发送的缓存策略。</param><returns>异步测试任务。</returns>
    [Theory]
    [MemberData(nameof(CacheCases))]
    public async Task DirectRequestsResolveCachePolicy(string api, bool simple, string environment,
        CacheRetention? explicitRetention, CacheRetention expected)
    {
        using var handler = CreateHandler();
        using var client = new HttpClient(handler);
        IStreamProvider provider = api switch
        {
            "openai-completions" => new OpenAiProvider(client),
            "openai-responses" => new OpenAiResponsesProvider(client),
            "anthropic-messages" => new AnthropicProvider(client),
            _ => new BedrockProvider(client)
        };
        var model = CreateModel(api);
        StreamOptions options = simple ? new SimpleStreamOptions() : new StreamOptions();
        options = options with
        {
            ApiKey = "synthetic-cache-test", SessionId = "cache-fixture", MaxRetries = 0,
            Env = new Dictionary<string, string>
            {
                ["PI_CACHE_RETENTION"] = environment,
                ["AWS_REGION"] = "us-east-1", ["AWS_BEDROCK_SKIP_AUTH"] = "0"
            }
        };
        if (explicitRetention.HasValue) options = options with { CacheRetention = explicitRetention.Value };

        // 1. 【AI】【实际报文】使用真实提供方组装路径，仅在 HTTP 传输层返回确定性错误
        var stream = simple
            ? provider.StreamSimple(model, new() { SystemPrompt = "system", Messages = [new UserMessage("hello")] }, (SimpleStreamOptions)options)
            : provider.Stream(model, new() { SystemPrompt = "system", Messages = [new UserMessage("hello")] }, options);
        await OpenAiResponsesProviderTests.CollectAsync(stream);
        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        var root = body.RootElement;
        var enabled = expected != CacheRetention.None;
        if (api.StartsWith("openai-", StringComparison.Ordinal))
        {
            Assert.Equal(enabled, root.TryGetProperty("prompt_cache_key", out var key));
            if (enabled) Assert.Equal("cache-fixture", key.GetString());
            Assert.Equal(expected == CacheRetention.Long, root.TryGetProperty("prompt_cache_retention", out var ttl));
            if (expected == CacheRetention.Long) Assert.Equal("24h", ttl.GetString());
        }
        else
        {
            var marker = api == "anthropic-messages" ? "cache_control" : "cachePoint";
            var points = FindProperties(root, marker).ToArray();
            Assert.Equal(enabled, points.Length > 0);
            foreach (var point in points)
            {
                Assert.Equal(expected == CacheRetention.Long, point.TryGetProperty("ttl", out var ttl));
                if (expected == CacheRetention.Long) Assert.Equal("1h", ttl.GetString());
            }
        }
        // 2. 【AI】【调用边界】默认值只写入内部副本，不能污染可重用的调用方选项
        Assert.Equal(explicitRetention.HasValue, options.HasExplicitCacheRetention);
        Assert.Equal(explicitRetention ?? CacheRetention.None, options.CacheRetention);
    }

    /// <summary>【AI】【Responses上限】验证禁用兼容开关后普通和简化入口均不发送输出令牌上限。</summary>
    /// <param name="simple">是否调用简化入口。</param><param name="supported">兼容开关。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(false, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(true, null)]
    public async Task ResponsesHonorsMaxOutputTokensCompatibility(bool simple, bool? supported)
    {
        using var handler = CreateHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(client);
        var model = CreateModel("openai-responses") with { Compat = new() { SupportsMaxOutputTokens = supported } };
        var context = new LlmContext { Messages = [new UserMessage("hi")] };
        var options = new SimpleStreamOptions { ApiKey = "synthetic-test", MaxRetries = 0, MaxTokens = 345 };
        await OpenAiResponsesProviderTests.CollectAsync(simple
            ? provider.StreamSimple(model, context, options)
            : provider.Stream(model, context, options));
        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        Assert.Equal(supported != false, body.RootElement.TryGetProperty("max_output_tokens", out var tokens));
        if (supported != false) Assert.Equal(345, tokens.GetInt32());
    }

    /// <summary>【AI】【缓存副本】检查派生选项经默认值归一化后仍保留协议专有字段。</summary>
    [Fact]
    public void CacheNormalizationPreservesDerivedOptions()
    {
        var options = new OpenAiResponsesOptions
        {
            Env = new Dictionary<string, string> { ["PI_CACHE_RETENTION"] = "long" },
            ReasoningEffort = "high"
        };
        var resolved = Assert.IsType<OpenAiResponsesOptions>(StreamOptionHelpers.WithCacheDefaults(options));
        Assert.Equal("high", resolved.ReasoningEffort);
        Assert.Equal(CacheRetention.Long, resolved.CacheRetention);
        Assert.False(options.HasExplicitCacheRetention);
        Assert.Same(resolved, StreamOptionHelpers.WithCacheDefaults(resolved));
    }

    /// <summary>【AI】【协议夹具】创建支持缓存、且全部请求由内存传输层拦截的模型。</summary>
    /// <param name="api">协议名称。</param><returns>用于请求组装的模型。</returns>
    private static Model CreateModel(string api) => new()
    {
        Name = "Cache test model",
        Id = api switch
        {
            "anthropic-messages" => "claude-sonnet-4",
            "bedrock-converse-stream" => "anthropic.claude-3-7-sonnet-20250219-v1:0",
            _ => "gpt-5.4"
        },
        Api = api,
        Provider = api switch { "anthropic-messages" => "anthropic", "bedrock-converse-stream" => "amazon-bedrock", _ => "openai" },
        BaseUrl = api switch
        {
            "anthropic-messages" => "https://api.anthropic.com",
            "bedrock-converse-stream" => "https://bedrock-runtime.us-east-1.amazonaws.com",
            _ => "https://api.openai.com/v1"
        },
        MaxOutputTokens = 4096,
        Compat = new() { SupportsLongCacheRetention = true }
    };

    /// <summary>【AI】【传输夹具】捕获请求后返回不会重试的响应，避免调用外部服务。</summary>
    /// <returns>内存 HTTP 处理器。</returns>
    private static OpenAiResponsesProviderTests.StubHandler CreateHandler() => new(_ => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
    });

    /// <summary>【AI】【缓存断言】递归查找请求中所有同名缓存标记，覆盖系统和消息边界。</summary>
    /// <param name="element">当前 JSON 节点。</param><param name="name">要查找的属性名。</param>
    /// <returns>匹配的属性值。</returns>
    private static IEnumerable<JsonElement> FindProperties(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name)) yield return property.Value;
                foreach (var value in FindProperties(property.Value, name)) yield return value;
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var value in FindProperties(item, name)) yield return value;
    }
}
