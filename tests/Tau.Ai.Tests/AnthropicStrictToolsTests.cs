// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Registry;
using Tau.Ai.Serialization;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Anthropic 严格工具回归】通过实际 HTTP 序列化验证策略、Schema、内联声明与配置。</summary>
public sealed class AnthropicStrictToolsTests
{
    private const string OptionalSchema = """
        {"type":"object","title":"LookupInput","description":"Root description","properties":{"value":{"type":"string"},"optional":{"type":"number"}},"required":["value"]}
        """;

    /// <summary>所有公开入口均按工具配置启用 strict，保留根级元数据并转换可选字段。</summary>
    /// <param name="entry">请求入口。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("direct")]
    [InlineData("direct-simple")]
    [InlineData("stream")]
    [InlineData("simple")]
    [InlineData("models")]
    [InlineData("models-simple")]
    public async Task Stream_StrictAcrossEntryPoints(string entry)
    {
        var tool = Tool(OptionalSchema);
        var capture = await SendAsync(new LlmContext("base", [new UserMessage("hi")], [tool]), Enabled(), entry);
        var definition = Definition(capture);
        Assert.True(definition.GetProperty("strict").GetBoolean());
        Assert.True(definition.GetProperty("eager_input_streaming").GetBoolean());
        var schema = definition.GetProperty("input_schema");
        Assert.Equal("LookupInput", schema.GetProperty("title").GetString());
        Assert.Equal("Root description", schema.GetProperty("description").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "value", "optional" }, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("null", schema.GetProperty("properties").GetProperty("optional").GetProperty("anyOf")[1].GetProperty("type").GetString());
        Assert.Equal(OptionalSchema, tool.ParameterSchema.GetRawText());
    }

    /// <summary>Anthropic 缺省和显式禁用 strict 时 prefer 回退，OpenAI 的 strictMode 标记不能代替 strictTools。</summary>
    /// <param name="supports">Anthropic 严格工具能力。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Stream_PreferFallsBackWithoutStrictToolsCapability(bool? supports)
    {
        var capture = await SendAsync(Context(Tool(OptionalSchema)), new() { SupportsStrictTools = supports, SupportsStrictMode = true });
        AssertLegacy(Definition(capture), Json(OptionalSchema));
    }

    /// <summary>未配置 JSON Schema 约束和 grammar 工具保持非 strict 的三字段结构。</summary>
    /// <param name="type">约束配置类型。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("grammar")]
    public async Task Stream_DoesNotEnableStrictWithoutOptIn(string? type)
    {
        var tool = Tool(OptionalSchema) with { ConstrainedSampling = type is null ? null : new() { Type = type, Strict = "require" } };
        AssertLegacy(Definition(await SendAsync(Context(tool), Enabled())), tool.ParameterSchema);
    }

    /// <summary>空对象缺省补 properties 与 required，严格模式额外关闭额外属性。</summary>
    /// <param name="supports">是否支持严格模式。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_EmptySchemasHaveProtocolDefaults(bool supports)
    {
        var schema = Definition(await SendAsync(Context(Tool("{\"type\":\"object\"}")), new() { SupportsStrictTools = supports })).GetProperty("input_schema");
        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
        Assert.Empty(schema.GetProperty("required").EnumerateArray());
        Assert.Equal(supports, schema.TryGetProperty("additionalProperties", out _));
    }

    /// <summary>Anthropic 不支持的关键字在 prefer 下保留原字段并回退，require 在载荷回调和 HTTP 前报错。</summary>
    /// <param name="property">不支持的字段 Schema。</param>
    /// <param name="reason">错误原因关键字。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("{\"type\":\"number\",\"minimum\":1}", "minimum")]
    [InlineData("{\"type\":\"number\",\"maximum\":5}", "maximum")]
    [InlineData("{\"type\":\"number\",\"exclusiveMinimum\":0}", "exclusiveMinimum")]
    [InlineData("{\"type\":\"number\",\"exclusiveMaximum\":5}", "exclusiveMaximum")]
    [InlineData("{\"type\":\"number\",\"multipleOf\":2}", "multipleOf")]
    [InlineData("{\"type\":\"array\",\"maxItems\":5}", "maxItems")]
    [InlineData("{\"type\":\"array\",\"uniqueItems\":false}", "uniqueItems")]
    [InlineData("{\"type\":\"array\",\"minContains\":1}", "minContains")]
    [InlineData("{\"type\":\"array\",\"maxContains\":5}", "maxContains")]
    [InlineData("{\"type\":\"object\",\"minProperties\":1}", "minProperties")]
    [InlineData("{\"type\":\"object\",\"maxProperties\":5}", "maxProperties")]
    [InlineData("{\"type\":\"array\",\"minItems\":2}", "minItems")]
    [InlineData("{\"type\":\"array\",\"minItems\":\"1\"}", "minItems")]
    [InlineData("{\"type\":\"array\",\"minItems\":null}", "minItems")]
    [InlineData("{\"type\":\"string\",\"format\":\"regex\"}", "format")]
    [InlineData("{\"type\":\"string\",\"format\":\"URI\"}", "format")]
    [InlineData("{\"type\":\"string\",\"format\":7}", "format")]
    [InlineData("{\"type\":\"string\",\"format\":null}", "format")]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"number\",\"minimum\":1}}", "minimum")]
    [InlineData("{\"anyOf\":[{\"type\":\"string\",\"format\":\"regex\"},{\"type\":\"null\"}]}", "format")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"options\":{\"type\":\"array\",\"minItems\":2}}}", "minItems")]
    [InlineData("{\"$ref\":\"#/definitions/item\"}", "$ref")]
    public async Task Stream_UnsupportedConstraintsHonorPolicy(string property, string reason)
    {
        var tool = Tool("{\"type\":\"object\",\"properties\":{\"value\":" + property + "}}");
        AssertLegacy(Definition(await SendAsync(Context(tool), Enabled())), tool.ParameterSchema);
        var required = tool with { ConstrainedSampling = new() { Type = "json_schema", Strict = "require" } };
        var capture = await SendAsync(Context(required), Enabled());
        AssertRejected(capture, reason);
        Assert.Contains("Tool \"lookup\" requires JSON-schema constrained sampling", capture.Result.ErrorMessage);
        Assert.Equal(property, tool.ParameterSchema.GetProperty("properties").GetProperty("value").GetRawText());
    }

    /// <summary>支持的字符串格式及长度、正则、数组下限可以启用 strict。</summary>
    /// <param name="format">支持的字符串格式。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("date-time")]
    [InlineData("time")]
    [InlineData("date")]
    [InlineData("duration")]
    [InlineData("email")]
    [InlineData("hostname")]
    [InlineData("uri")]
    [InlineData("ipv4")]
    [InlineData("ipv6")]
    [InlineData("uuid")]
    public async Task Stream_SupportedFormatsAndConstraintsRemainStrict(string format)
    {
        var tool = Tool("{\"type\":\"object\",\"properties\":{\"code\":{\"type\":\"string\",\"format\":\"" + format + "\",\"minLength\":1,\"maxLength\":1000,\"pattern\":\"^[a-z]+$\"},\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":1.0},\"empty\":{\"type\":\"array\",\"minItems\":0}},\"required\":[\"code\",\"tags\",\"empty\"]}");
        var definition = Definition(await SendAsync(Context(tool), Enabled()));
        Assert.True(definition.GetProperty("strict").GetBoolean());
        var properties = definition.GetProperty("input_schema").GetProperty("properties");
        Assert.Equal(format, properties.GetProperty("code").GetProperty("format").GetString());
        Assert.Equal("^[a-z]+$", properties.GetProperty("code").GetProperty("pattern").GetString());
        Assert.Equal(1, properties.GetProperty("tags").GetProperty("minItems").GetDouble());
        Assert.Equal(0, properties.GetProperty("empty").GetProperty("minItems").GetDouble());
    }

    /// <summary>普通属性名和示例可以包含限制关键字，不应触发 Schema 关键字回退。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Stream_KeywordLikePropertyNamesDoNotDisableStrict()
    {
        var tool = Tool("""
            {"type":"object","properties":{"format":{"type":"string","default":"regex"},"minimum":{"type":"number"},"allOf":{"type":"boolean"}},"examples":[{"format":"regex","minimum":1,"allOf":true}]}
            """);
        Assert.True(Definition(await SendAsync(Context(tool), Enabled())).GetProperty("strict").GetBoolean());
    }

    /// <summary>顶层与内联工具共用严格策略，缓存和即时输入标记仍由各自能力控制。</summary>
    /// <param name="supports">严格工具能力。</param>
    /// <param name="streamAndCache">是否启用即时输入和工具定义缓存。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Stream_NativeDefinitionsShareStrictConversion(bool supports, bool streamAndCache)
    {
        var tool = Tool(OptionalSchema);
        var context = new LlmContext(null,
        [
            new SystemMessage("base") { ToolsAdded = [tool] }, new UserMessage("before"),
            new SystemMessage("") { ToolsAdded = [tool with { Name = "later" }] }
        ], null);
        var compat = Enabled() with { SupportsStrictTools = supports, SupportsMidConvoSystemMessages = true, SupportsMidConvoToolChanges = true, SupportsEagerToolInputStreaming = streamAndCache, SupportsCacheControlOnTools = streamAndCache };
        var capture = await SendAsync(context, compat);
        var initial = Definition(capture);
        var body = capture.Body!.Value;
        var placeholder = body.GetProperty("tools")[1];
        var addition = body.GetProperty("messages")[1].GetProperty("content")[0];
        var inline = addition.GetProperty("tool").GetProperty("definition");
        Assert.True(JsonElement.DeepEquals(initial.GetProperty("input_schema"), inline.GetProperty("input_schema")));
        Assert.Equal(supports, initial.TryGetProperty("strict", out _));
        Assert.Equal(supports, inline.TryGetProperty("strict", out _));
        Assert.Equal(streamAndCache, initial.TryGetProperty("eager_input_streaming", out _));
        Assert.Equal(streamAndCache, inline.TryGetProperty("eager_input_streaming", out _));
        Assert.Equal(streamAndCache, initial.TryGetProperty("cache_control", out _));
        Assert.False(inline.TryGetProperty("cache_control", out _));
        Assert.True(addition.TryGetProperty("cache_control", out _));
        Assert.False(placeholder.TryGetProperty("strict", out _));
        Assert.False(placeholder.TryGetProperty("cache_control", out _));
        Assert.False(placeholder.TryGetProperty("eager_input_streaming", out _));
        Assert.Equal(OptionalSchema, tool.ParameterSchema.GetRawText());
    }

    /// <summary>中途新增 require 工具在能力缺失或 Schema 不支持时同样阻止整个请求。</summary>
    /// <param name="supports">模型是否支持 strict。</param>
    /// <param name="simple">是否使用简化入口。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Stream_InlineRequireFailurePreventsRequest(bool supports, bool simple)
    {
        var initial = Tool(OptionalSchema) with { ConstrainedSampling = null };
        var required = Tool(supports ? "{\"type\":\"object\",\"maxProperties\":1}" : OptionalSchema, "require") with { Name = "later" };
        var context = new LlmContext(null, [new SystemMessage("base") { ToolsAdded = [initial] }, new UserMessage("hi"), new SystemMessage("") { ToolsAdded = [required] }], null);
        var capture = await SendAsync(context, Enabled() with { SupportsStrictTools = supports, SupportsMidConvoSystemMessages = true, SupportsMidConvoToolChanges = true }, simple ? "direct-simple" : "direct");
        AssertRejected(capture, supports ? "maxProperties" : "strict tools are unsupported");
        Assert.Contains("Tool \"later\"", capture.Result.ErrorMessage);
    }

    /// <summary>只校验实际发送的声明；折叠模式可丢弃已移除的非法工具，原生模式仍需校验开场声明。</summary>
    /// <param name="native">是否保留原生工具变更。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_ValidatesOnlyEmittedDeclarations(bool native)
    {
        var invalid = Tool("{\"type\":\"object\",\"maxProperties\":1}", "require");
        var context = new LlmContext(null, [new SystemMessage("base") { ToolsAdded = [invalid] }, new UserMessage("hi"), new SystemMessage("") { ToolsRemoved = [new("lookup")], ToolsAdded = [Tool(OptionalSchema) with { Name = "current" }] }], null);
        var capture = await SendAsync(context, Enabled() with { SupportsMidConvoSystemMessages = native, SupportsMidConvoToolChanges = native });
        if (native) AssertRejected(capture, "maxProperties");
        else Assert.Equal("current", Definition(capture).GetProperty("name").GetString());
    }

    /// <summary>strictTools 可从供应商继承、由模型禁用并经源生成序列化恢复，最后影响实际请求。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Configuration_StrictToolsSurvivesInheritanceAndRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-anthropic-strict-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {"providers":{"test":{"api":"anthropic-messages","compat":{"supportsStrictTools":true},"models":[{"id":"enabled"},{"id":"disabled","compat":{"supportsStrictTools":false}}]}}}
                """);
            var models = new ModelConfigurationStore([path]).ApplyToModels([], ModelTypes.Chat);
            foreach (var model in models)
            {
                var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(model, TauAiJsonContext.Default.Model), TauAiJsonContext.Default.Model)!;
                Assert.Equal(model.Id == "enabled", restored.Compat!.SupportsStrictTools);
                var definition = Definition(await SendAsync(Context(Tool(OptionalSchema)), restored.Compat));
                Assert.Equal(model.Id == "enabled", definition.TryGetProperty("strict", out _));
            }
            Assert.Equal(2, models.Count);
        }
        finally { File.Delete(path); }
    }

    /// <summary>检查回退报文恰好保留 legacy 三字段且不修改字段 Schema。</summary>
    /// <param name="definition">序列化后的工具定义。</param>
    /// <param name="original">调用方原始 Schema。</param>
    private static void AssertLegacy(JsonElement definition, JsonElement original)
    {
        Assert.False(definition.TryGetProperty("strict", out _));
        var schema = definition.GetProperty("input_schema");
        Assert.Equal(new[] { "type", "properties", "required" }, schema.EnumerateObject().Select(property => property.Name));
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(JsonElement.DeepEquals(original.GetProperty("properties"), schema.GetProperty("properties")));
        Assert.True(JsonElement.DeepEquals(original.TryGetProperty("required", out var required) ? required : Json("[]"), schema.GetProperty("required")));
    }

    /// <summary>检查失败只产生终止错误，没有执行载荷回调或发送 HTTP。</summary>
    /// <param name="capture">请求执行结果。</param>
    /// <param name="reason">预期原因。</param>
    private static void AssertRejected(Capture capture, string reason)
    {
        Assert.Equal(StopReason.Error, capture.Result.StopReason);
        Assert.Contains(reason, capture.Result.ErrorMessage);
        Assert.IsType<ErrorEvent>(Assert.Single(capture.Events));
        Assert.Equal(0, capture.HttpCount);
        Assert.Equal(0, capture.PayloadCount);
        Assert.Null(capture.Body);
    }

    /// <summary>检查请求成功并提取首个工具定义。</summary>
    /// <param name="capture">请求执行结果。</param>
    /// <returns>首个工具定义。</returns>
    private static JsonElement Definition(Capture capture)
    {
        Assert.Equal(StopReason.EndTurn, capture.Result.StopReason);
        Assert.Equal(1, capture.HttpCount);
        Assert.Equal(1, capture.PayloadCount);
        return capture.Body!.Value.GetProperty("tools")[0];
    }

    /// <summary>创建首条系统消息声明工具的上下文。</summary>
    /// <param name="tool">工具定义。</param>
    /// <returns>最小消息式上下文。</returns>
    private static LlmContext Context(Tool tool) => new(null, [new SystemMessage("base") { ToolsAdded = [tool] }, new UserMessage("hi")], null);

    /// <summary>创建显式支持严格工具的能力配置。</summary>
    /// <returns>严格工具能力。</returns>
    private static ModelCompatibility Enabled() => new() { SupportsStrictTools = true };

    /// <summary>创建 JSON Schema 约束工具。</summary>
    /// <param name="schema">完整参数 Schema 文本。</param>
    /// <param name="policy">严格策略。</param>
    /// <returns>独立工具定义。</returns>
    private static Tool Tool(string schema, string policy = "prefer") => new("lookup", "Lookup description", Json(schema)) { ConstrainedSampling = new() { Type = "json_schema", Strict = policy } };

    /// <summary>解析独立 JSON 值。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>文档释放后仍有效的 JSON。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    /// <summary>【AI】【Anthropic 严格工具回归】用模拟 SSE 执行完整入口链，收集报文、事件和调用次数。</summary>
    /// <param name="context">消息上下文。</param>
    /// <param name="compat">模型能力。</param>
    /// <param name="entry">公开入口。</param>
    /// <returns>成功或失败的完整执行快照。</returns>
    private static async Task<Capture> SendAsync(LlmContext context, ModelCompatibility compat, string entry = "direct")
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"strict","usage":{"input_tokens":1,"output_tokens":0}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

            event: message_stop
            data: {"type":"message_stop"}

            """));
        using var client = new HttpClient(handler);
        IStreamProvider provider = new AnthropicProvider(client);
        var model = new Model { Id = "strict-test", Name = "Strict test", Api = provider.Api, Provider = "strict-test", BaseUrl = "https://example.invalid", Compat = compat };
        var payloadCount = 0;
        var options = new SimpleStreamOptions { ApiKey = "test-key", CacheRetention = CacheRetention.Short, OnPayload = (_, _) => { payloadCount++; return ValueTask.FromResult<object?>(null); } };
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var models = new Models([new ProviderDefinition(model.Provider, provider, [model])]);
        var stream = entry switch
        {
            "direct-simple" => provider.StreamSimple(model, context, options),
            "stream" => StreamFunctions.Stream(registry, model, context, options),
            "simple" => StreamFunctions.StreamSimple(registry, model, context, options),
            "models" => models.Stream(model, context, options),
            "models-simple" => models.StreamSimple(model, context, options),
            _ => provider.Stream(model, context, options)
        };
        var events = new List<StreamEvent>();
        await foreach (var item in stream) events.Add(item);
        return new(await stream.ResultAsync, events, handler.Requests.Count, payloadCount, handler.Requests.Count == 0 ? null : Json(handler.CapturedBody));
    }

    private sealed record Capture(AssistantMessage Result, List<StreamEvent> Events, int HttpCount, int PayloadCount, JsonElement? Body);
}
