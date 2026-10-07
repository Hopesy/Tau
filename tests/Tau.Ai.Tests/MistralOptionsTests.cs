// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Mistral;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【Mistral】【选项回归】验证模型映射、通用工具选择和缓存覆盖的实际 HTTP 报文。</summary>
public sealed class MistralOptionsTests
{
    /// <summary>【Mistral】【思考等级】通过直接及统一入口覆盖元数据映射、关闭与等级钳制。</summary>
    /// <param name="dispatch">是否通过统一入口调用。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThinkingUsesMetadataThroughBothEntries(bool dispatch)
    {
        var cases = new (bool Reasoning, Dictionary<string, string?>? Map, ThinkingLevel? Level, string? Mode, string? Effort)[]
        {
            (true, null, ThinkingLevel.High, "reasoning", null),
            (true, null, ThinkingLevel.Off, null, null),
            (true, null, null, null, null),
            (false, new() { ["high"] = "high" }, ThinkingLevel.High, null, null),
            (true, new() { ["high"] = "custom" }, ThinkingLevel.High, null, "custom"),
            (true, new() { ["off"] = "none" }, null, null, "none"),
            (true, new() { ["off"] = "none" }, ThinkingLevel.Off, null, "none"),
            (true, new() { ["off"] = null }, ThinkingLevel.Off, null, "high"),
            (true, new() { ["low"] = null, ["medium"] = "mapped-medium" }, ThinkingLevel.Low, null, "mapped-medium"),
            (true, new() { ["high"] = "mapped-high" }, ThinkingLevel.ExtraHigh, null, "mapped-high"),
            (true, new(), ThinkingLevel.Minimal, null, "high")
        };
        foreach (var item in cases)
        {
            var model = Model() with { Reasoning = item.Reasoning, ThinkingLevelMap = item.Map };
            var (body, _) = await CaptureAsync(model, new SimpleStreamOptions { Reasoning = item.Level }, dispatch ? 2 : 1);
            Assert.Equal(item.Mode, Property(body, "prompt_mode"));
            Assert.Equal(item.Effort, Property(body, "reasoning_effort"));
        }
    }

    /// <summary>【Mistral】【工具选择】字符串、原生类型与 JSON 命名函数均通过真实请求序列化。</summary>
    /// <param name="dispatch">是否通过统一入口调用。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolChoiceSurvivesBothEntries(bool dispatch)
    {
        var choices = new object[] { "required", MistralToolChoice.Function("read_file"),
            JsonDocument.Parse("\"none\"").RootElement.Clone(),
            JsonDocument.Parse("""{"type":"function","function":{"name":"read_file"}}""").RootElement.Clone() };
        foreach (var choice in choices)
        {
            var (body, _) = await CaptureAsync(Model(), new SimpleStreamOptions { ToolChoice = choice }, dispatch ? 2 : 1);
            var actual = body.GetProperty("tool_choice");
            if (actual.ValueKind == JsonValueKind.String)
                Assert.Equal(choice is string ? "required" : "none", actual.GetString());
            else
            {
                Assert.Equal("function", actual.GetProperty("type").GetString());
                Assert.Equal("read_file", actual.GetProperty("function").GetProperty("name").GetString());
            }
        }
    }

    /// <summary>【Mistral】【缓存会话】未设置、关闭和显式缓存策略在三个入口保持相同语义。</summary>
    /// <param name="entry">普通、直接简化或统一简化入口。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CacheRetentionControlsBodyAndAffinity(int entry)
    {
        foreach (var retention in new CacheRetention?[] { null, CacheRetention.None, CacheRetention.Short, CacheRetention.Long })
        foreach (var session in new string?[] { null, "", "session-fixture" })
        {
            var options = new SimpleStreamOptions { SessionId = session };
            if (retention.HasValue) options = options with { CacheRetention = retention.Value };
            var (body, headers) = await CaptureAsync(Model(), options, entry);
            var enabled = !string.IsNullOrEmpty(session) && retention != CacheRetention.None;
            Assert.Equal(enabled, body.TryGetProperty("prompt_cache_key", out var key));
            Assert.Equal(enabled, headers.TryGetValue("x-affinity", out var affinity));
            if (enabled)
            {
                Assert.Equal(session, key.GetString());
                Assert.Equal(session, affinity);
            }
            Assert.Equal(retention.HasValue, options.HasExplicitCacheRetention);
        }
    }

    /// <summary>【Mistral】【请求头覆盖】验证大小写、显式删除、关闭缓存与最终回调的优先级。</summary>
    /// <param name="entry">普通、直接简化或统一简化入口。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AffinityHonorsExplicitHeadersAndFinalCallback(int entry)
    {
        var model = Model() with { Headers = new Dictionary<string, string> { ["X-Affinity"] = "model" } };
        var options = new SimpleStreamOptions { SessionId = "session" };
        var (_, modelHeaders) = await CaptureAsync(model, options, entry);
        Assert.Equal("model", modelHeaders["x-affinity"]);
        options = options with { Headers = new Dictionary<string, string> { ["x-AFFINITY"] = "request" } };
        var (_, requestHeaders) = await CaptureAsync(model, options, entry);
        Assert.Equal("request", requestHeaders["x-affinity"]);
        var (_, disabledHeaders) = await CaptureAsync(model, options with { CacheRetention = CacheRetention.None }, entry);
        Assert.Equal("request", disabledHeaders["x-affinity"]);
        var (_, deletedHeaders) = await CaptureAsync(model, options with { Headers = new Dictionary<string, string> { ["x-affinity"] = null! } }, entry);
        Assert.False(deletedHeaders.ContainsKey("x-affinity"));
        var (_, deletedModelHeaders) = await CaptureAsync(model with { Headers = new Dictionary<string, string> { ["X-Affinity"] = null! } }, new SimpleStreamOptions { SessionId = "session" }, entry);
        Assert.False(deletedModelHeaders.ContainsKey("x-affinity"));
        var (_, finalHeaders) = await CaptureAsync(model, options with
        {
            TransformHeaders = (headers, _) =>
            {
                Assert.Equal("request", headers["x-affinity"]);
                headers["x-affinity"] = "final";
                return ValueTask.FromResult<IDictionary<string, string?>?>(headers);
            }
        }, entry);
        Assert.Equal("final", finalHeaders["x-affinity"]);
    }

    /// <summary>【Mistral】【测试模型】任意模型名称必须按能力元数据解析思考选项。</summary>
    /// <returns>合成模型。</returns>
    private static Model Model() => new() { Id = "future-model", Name = "Mistral fixture", Api = "mistral-conversations", Provider = "mistral", BaseUrl = "https://api.mistral.ai/v1" };

    /// <summary>【Mistral】【字段断言】读取可选的字符串属性。</summary>
    /// <param name="body">请求报文。</param><param name="name">属性名。</param><returns>属性值或 null。</returns>
    private static string? Property(JsonElement body, string name) => body.TryGetProperty(name, out var value) ? value.GetString() : null;

    /// <summary>【Mistral】【请求捕获】运行实际提供方并在传输层拦截请求。</summary>
    /// <param name="model">目标模型。</param><param name="options">请求选项。</param><param name="entry">入口编号。</param>
    /// <returns>请求 JSON 和不区分大小写的请求头。</returns>
    private static async Task<(JsonElement Body, Dictionary<string, string> Headers)> CaptureAsync(Model model, SimpleStreamOptions options, int entry)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            foreach (var header in request.Headers) headers[header.Key] = string.Join(",", header.Value);
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("fixture") };
        });
        using var client = new HttpClient(handler);
        var provider = new MistralProvider(client);
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, provider);
        options = options with { ApiKey = "synthetic-key", MaxRetries = 0 };
        var context = new LlmContext { Messages = [new UserMessage("hello")] };
        var stream = entry switch
        {
            0 => provider.Stream(model, context, options),
            1 => provider.StreamSimple(model, context, options),
            _ => StreamFunctions.StreamSimple(registry, model, context, options, new ModelConfigurationStore([]))
        };
        await OpenAiResponsesProviderTests.CollectAsync(stream);
        Assert.Single(handler.Requests);
        using var document = JsonDocument.Parse(handler.CapturedBody);
        return (document.RootElement.Clone(), headers);
    }
}
