// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Registry;
using Tau.Ai.Serialization;

namespace Tau.Ai.Tests;

/// <summary>【AI】【OpenAI 严格工具回归】通过真实请求转换验证五种实现的工具策略和声明位置。</summary>
public sealed class OpenAiStrictToolsTests
{
    private static readonly string[] Apis = ["chat", "compatible", "responses", "azure", "codex"];
    private const string Schema = """{"type":"object","description":"root metadata","properties":{"path":{"type":"string"},"settings":{"type":"object","properties":{"mode":{"type":"string"}}}},"required":["path"]}""";
    private const string Unsupported = """{"type":"object","properties":{"value":{"oneOf":[{"type":"string"},{"type":"number"}]}}}""";

    /// <summary>列出五种协议与六个公开请求入口的组合。</summary>
    /// <returns>协议和入口测试数据。</returns>
    public static IEnumerable<object[]> EntryCases()
    {
        foreach (var api in Apis)
            foreach (var entry in new[] { "direct", "direct-simple", "stream", "simple", "models", "models-simple" })
                yield return [api, entry];
    }

    /// <summary>公开入口均使用严格规范化副本，并保留根元数据和原始 Schema。</summary>
    /// <param name="api">协议实现。</param>
    /// <param name="entry">公开入口。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [MemberData(nameof(EntryCases))]
    public async Task Request_NormalizesRequiredTool(string api, string entry)
    {
        var tool = Tool(Schema, "require");
        var capture = await SendAsync(api, Context(tool), new() { SupportsStrictMode = true }, entry);
        var definition = Definition(Assert.Single(Body(capture).GetProperty("tools").EnumerateArray()), api);
        AssertStrict(definition);
        Assert.Equal("root metadata", definition.GetProperty("parameters").GetProperty("description").GetString());
        Assert.Equal(Schema, tool.ParameterSchema.GetRawText());
        Assert.Equal(1, capture.PayloadCount);
    }

    /// <summary>prefer 对不兼容 Schema 或缺失能力回退，未配置和 grammar 不会自动开启 strict。</summary>
    /// <param name="api">协议实现。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    public async Task Request_PreservesNonStrictFallbacks(string api)
    {
        var tools = new[]
        {
            Tool(Unsupported) with { Name = "unsupported" },
            Tool(Schema) with { Name = "plain", ConstrainedSampling = null },
            Tool(Schema) with { Name = "grammar", ConstrainedSampling = new() { Type = "grammar", Variants = new Dictionary<string, string> { ["openai_lark"] = "start: WORD" } } }
        };
        foreach (var supports in new[] { true, false })
        {
            var capture = await SendAsync(api, new(null, [new UserMessage("hi")], tools), new() { SupportsStrictMode = supports });
            var definitions = Body(capture).GetProperty("tools").EnumerateArray().Select(item => Definition(item, api)).ToArray();
            for (var index = 0; index < tools.Length; index++)
            {
                Assert.True(JsonElement.DeepEquals(tools[index].ParameterSchema, definitions[index].GetProperty("parameters")));
                Assert.Equal(supports, definitions[index].TryGetProperty("strict", out var strict));
                if (supports) Assert.Equal(api == "codex" ? JsonValueKind.Null : JsonValueKind.False, strict.ValueKind);
            }
        }
        var valid = await SendAsync(api, Context(Tool(Schema)), new() { SupportsStrictMode = false });
        var fallback = Definition(Body(valid).GetProperty("tools")[0], api);
        Assert.False(fallback.TryGetProperty("strict", out _));
        Assert.True(JsonElement.DeepEquals(Json(Schema), fallback.GetProperty("parameters")));
    }

    /// <summary>require 在能力缺失或 Schema 不兼容时返回错误终态，载荷回调和 HTTP 均不能执行。</summary>
    /// <param name="api">协议实现。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    public async Task Request_RejectsRequireBeforePayloadAndHttp(string api)
    {
        foreach (var entry in new[] { "direct", "simple" })
        {
            AssertRejected(await SendAsync(api, Context(Tool(Schema, "require")), new() { SupportsStrictMode = false }, entry), "strict tools are unsupported");
            AssertRejected(await SendAsync(api, Context(Tool(Unsupported, "require")), new() { SupportsStrictMode = true }, entry), "oneOf schemas are unsupported");
        }
    }

    /// <summary>strict 能力默认值由协议决定，Anthropic 的 supportsStrictTools 不能替代 OpenAI 标记。</summary>
    /// <param name="api">协议实现。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    public async Task Request_UsesProtocolCapabilityDefaults(string api)
    {
        var capture = await SendAsync(api, Context(Tool(Schema, "require")), new() { SupportsStrictTools = true });
        if (api is "azure" or "codex") AssertStrict(Definition(Body(capture).GetProperty("tools")[0], api));
        else AssertRejected(capture, "strict tools are unsupported");
    }

    /// <summary>原生新增声明和开场声明使用相同的严格转换，内联 require 失败也不能发送请求。</summary>
    /// <param name="api">协议实现。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    public async Task NativeAdditions_ApplyStrictAndRejectInvalidSchema(string api)
    {
        var capture = await SendAsync(api, AdditionContext(Tool(Schema, "require")), Native());
        var body = Body(capture);
        var addition = Assert.Single(body.GetProperty(IsChat(api) ? "messages" : "input").EnumerateArray(), item => item.TryGetProperty("tools", out _));
        AssertStrict(Definition(addition.GetProperty("tools")[0], api));
        AssertStrict(Definition(body.GetProperty("tools")[0], api));
        AssertRejected(await SendAsync(api, AdditionContext(Tool(Unsupported, "require")), Native()), "oneOf schemas are unsupported");
    }

    /// <summary>Responses 客户端工具搜索输出仍严格规范化，并保留 defer_loading 标记。</summary>
    /// <param name="api">Responses 协议实现。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    public async Task ToolSearch_UsesStrictConversion(string api)
    {
        var compat = Native() with { SupportsAdditionalTools = false, SupportsToolSearch = true };
        var body = Body(await SendAsync(api, AdditionContext(Tool(Schema)), compat));
        var result = Assert.Single(body.GetProperty("input").EnumerateArray(), item => item.TryGetProperty("type", out var type) && type.GetString() == "tool_search_output");
        var tool = Assert.Single(result.GetProperty("tools").EnumerateArray());
        AssertStrict(tool);
        Assert.True(tool.GetProperty("defer_loading").GetBoolean());
        Assert.False(body.GetProperty("tools")[0].TryGetProperty("defer_loading", out _));
        AssertRejected(await SendAsync(api, AdditionContext(Tool(Unsupported, "require")), compat), "oneOf schemas are unsupported");
    }

    /// <summary>移除或重定义触发声明合并时只检查最终发送的工具，不校验已消失的非法历史定义。</summary>
    /// <param name="api">协议实现。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    public async Task FoldedChanges_ValidateOnlyEffectiveTools(string api)
    {
        var context = new LlmContext(null, [new SystemMessage("base") { ToolsAdded = [Tool(Unsupported, "require")] },
            new UserMessage("before"), new SystemMessage("update") { ToolsRemoved = [new("lookup")], ToolsAdded = [Tool(Schema, "require")] },
            new UserMessage("after")], null);
        var body = Body(await SendAsync(api, context, Native()));
        AssertStrict(Definition(Assert.Single(body.GetProperty("tools").EnumerateArray()), api));
        Assert.DoesNotContain(body.GetProperty(IsChat(api) ? "messages" : "input").EnumerateArray(), item => item.TryGetProperty("tools", out _));
    }

    /// <summary>Responses 公共转换器保留 false/null 默认值区别，并让工具显式策略优先。</summary>
    [Fact]
    public void ResponsesConverter_RespectsDefaultsAndToolOverrides()
    {
        var plain = Tool(Schema) with { ConstrainedSampling = null };
        var defaults = Assert.IsType<Dictionary<string, object>>(Assert.Single(OpenAiResponsesShared.ConvertResponsesTools([plain])));
        Assert.Equal(false, defaults["strict"]);
        var nullable = Assert.IsType<Dictionary<string, object>>(Assert.Single(OpenAiResponsesShared.ConvertResponsesTools([plain], strict: null)));
        Assert.True(nullable.ContainsKey("strict"));
        Assert.Null(nullable["strict"]);
        var forced = Assert.IsType<Dictionary<string, object>>(Assert.Single(OpenAiResponsesShared.ConvertResponsesTools([plain], strict: true)));
        Assert.False(((JsonElement)forced["parameters"]).GetProperty("additionalProperties").GetBoolean());
        var explicitTool = Assert.IsType<Dictionary<string, object>>(Assert.Single(OpenAiResponsesShared.ConvertResponsesTools([Tool(Schema)], strict: false)));
        Assert.Equal(true, explicitTool["strict"]);
    }

    /// <summary>配置文件能力继承、模型级覆盖、序列化往返及专用选项分发均影响实际报文。</summary>
    /// <param name="api">协议实现。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    public async Task Configuration_ControlsActualStrictRequests(string api)
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-openai-strict-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {"providers":{"strict-test":{"compat":{"supportsStrictMode":true},"options":{"toolChoice":"auto"},"models":[{"id":"enabled"},{"id":"disabled","compat":{"supportsStrictMode":false}}]}}}
                """);
            var store = new ModelConfigurationStore([path]);
            var models = store.ApplyToModels([], ModelTypes.Chat);
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(models.Single(model => model.Id == "enabled"), TauAiJsonContext.Default.Model), TauAiJsonContext.Default.Model)!;
            Assert.True(restored.Compat!.SupportsStrictMode);
            Assert.False(models.Single(model => model.Id == "disabled").Compat!.SupportsStrictMode);
            AssertStrict(Definition(Body(await SendAsync(api, Context(Tool(Schema, "require")), restored.Compat)).GetProperty("tools")[0], api));
            var enabled = await SendAsync(api, Context(Tool(Schema, "require")), restored.Compat, "simple", store, "enabled");
            AssertStrict(Definition(Body(enabled).GetProperty("tools")[0], api));
            AssertRejected(await SendAsync(api, Context(Tool(Schema, "require")), models.Single(model => model.Id == "disabled").Compat, "simple", store, "disabled"), "strict tools are unsupported");
        }
        finally { File.Delete(path); }
    }

    /// <summary>断言请求成功并取得报文。</summary>
    /// <param name="capture">执行快照。</param>
    /// <returns>请求报文。</returns>
    private static JsonElement Body(Capture capture)
    {
        Assert.True(capture.Result.StopReason != StopReason.Error, capture.Result.ErrorMessage);
        Assert.Equal(1, capture.HttpCount);
        return capture.Body!.Value;
    }

    /// <summary>检查严格 Schema 中的必填列表、可空属性及嵌套对象。</summary>
    /// <param name="definition">函数定义。</param>
    private static void AssertStrict(JsonElement definition)
    {
        Assert.True(definition.GetProperty("strict").GetBoolean());
        var parameters = definition.GetProperty("parameters");
        Assert.False(parameters.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "path", "settings" }, parameters.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var settings = parameters.GetProperty("properties").GetProperty("settings").GetProperty("anyOf");
        Assert.Equal("null", settings[1].GetProperty("type").GetString());
        Assert.False(settings[0].GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("mode", Assert.Single(settings[0].GetProperty("required").EnumerateArray()).GetString());
    }

    /// <summary>检查失败终态和错误事件，并确保报文回调和 HTTP 都未发生。</summary>
    /// <param name="capture">执行快照。</param>
    /// <param name="reason">具体不兼容原因。</param>
    private static void AssertRejected(Capture capture, string reason)
    {
        Assert.Equal(StopReason.Error, capture.Result.StopReason);
        Assert.Contains("Tool \"lookup\" requires JSON-schema constrained sampling", capture.Result.ErrorMessage);
        Assert.Contains(reason, capture.Result.ErrorMessage);
        Assert.Single(capture.Events.OfType<ErrorEvent>());
        Assert.Equal(0, capture.HttpCount);
        Assert.Equal(0, capture.PayloadCount);
    }

    /// <summary>从 Chat Completions 包装中取出函数声明。</summary>
    /// <param name="tool">工具报文。</param>
    /// <param name="api">协议实现。</param>
    /// <returns>函数定义。</returns>
    private static JsonElement Definition(JsonElement tool, string api) => IsChat(api) ? tool.GetProperty("function") : tool;

    /// <summary>判断协议是否使用 Chat Completions 格式。</summary>
    /// <param name="api">协议别名。</param>
    /// <returns>是否使用嵌套函数声明。</returns>
    private static bool IsChat(string api) => api is "chat" or "compatible";

    /// <summary>创建支持原生新增和 strict 的能力配置。</summary>
    /// <returns>模型兼容配置。</returns>
    private static ModelCompatibility Native() => new() { SupportsStrictMode = true, SupportsMidConvoSystemMessages = true, SupportsMidConvoToolAdditions = true, SupportsAdditionalTools = true };

    /// <summary>创建使用旧顶层工具字段的上下文。</summary>
    /// <param name="tool">待发送工具。</param>
    /// <returns>会话上下文。</returns>
    private static LlmContext Context(Tool tool) => new("base", [new UserMessage("hi")], [tool]);

    /// <summary>创建包含原生系统工具新增的会话。</summary>
    /// <param name="addition">后续新增的工具。</param>
    /// <returns>消息式上下文。</returns>
    private static LlmContext AdditionContext(Tool addition) => new(null, [new SystemMessage("base") { ToolsAdded = [Tool(Schema) with { Name = "initial" }] },
        new UserMessage("before"), new SystemMessage("update") { ToolsAdded = [addition] }, new UserMessage("after")], null);

    /// <summary>创建显式 JSON Schema 工具。</summary>
    /// <param name="schema">参数 Schema。</param>
    /// <param name="policy">严格模式策略。</param>
    /// <returns>工具声明。</returns>
    private static Tool Tool(string schema, string policy = "prefer") => new("lookup", "Lookup", Json(schema)) { ConstrainedSampling = new() { Type = "json_schema", Strict = policy } };

    /// <summary>解析独立持有内存的 JSON。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>JSON 副本。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    /// <summary>【AI】【OpenAI 严格工具回归】执行实际公开入口并收集 HTTP、载荷回调与流事件。</summary>
    /// <param name="api">协议别名。</param>
    /// <param name="context">请求上下文。</param>
    /// <param name="compat">模型能力。</param>
    /// <param name="entry">调用入口。</param>
    /// <param name="configuration">可选模型配置存储。</param>
    /// <param name="modelId">用于配置覆盖的模型标识。</param>
    /// <returns>请求和响应快照。</returns>
    private static async Task<Capture> SendAsync(string api, LlmContext context, ModelCompatibility? compat, string entry = "direct", ModelConfigurationStore? configuration = null, string modelId = "strict-model")
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(IsChat(api)
            ? "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"
            : "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\"}}\n\n"));
        using var client = new HttpClient(handler);
        IStreamProvider provider = api switch
        {
            "chat" => new OpenAiProvider(client),
            "compatible" => new OpenAiCompatibleProvider("test-compatible", "https://example.invalid/v1", httpClient: client),
            "responses" => new OpenAiResponsesProvider(client),
            "azure" => new AzureOpenAiResponsesProvider(client),
            "codex" => new OpenAiCodexResponsesProvider(client),
            _ => throw new ArgumentException("Unknown protocol", nameof(api))
        };
        using var lifetime = provider as IDisposable;
        var model = new Model { Id = modelId, Name = "Strict test", Api = provider.Api, Provider = "strict-test", BaseUrl = "https://example.invalid/v1", Compat = compat };
        var payloadCount = 0;
        var options = new SimpleStreamOptions
        {
            ApiKey = api == "codex" ? OpenAiResponsesSharedTests.BuildFakeJwt("strict-test") : "key", Transport = StreamTransport.Sse,
            OnPayload = (_, _) => { payloadCount++; return ValueTask.FromResult<object?>(null); }
        };
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var models = new Models([new ProviderDefinition(model.Provider, provider, [model])]);
        StreamOptions directOptions = api == "azure"
            ? new AzureOpenAiResponsesOptions { ApiKey = options.ApiKey, Transport = options.Transport, OnPayload = options.OnPayload }
            : options;
        var stream = entry switch
        {
            "direct-simple" => provider.StreamSimple(model, context, options),
            "stream" => StreamFunctions.Stream(registry, model, context, directOptions, configuration ?? new([])),
            "simple" => StreamFunctions.StreamSimple(registry, model, context, options, configuration ?? new([])),
            "models" => models.Stream(model, context, directOptions),
            "models-simple" => models.StreamSimple(model, context, options),
            _ => provider.Stream(model, context, directOptions)
        };
        var events = new List<StreamEvent>();
        await foreach (var item in stream) events.Add(item);
        return new(await stream.ResultAsync, events, handler.Requests.Count, payloadCount, handler.Requests.Count == 0 ? null : Json(handler.CapturedBody));
    }

    private sealed record Capture(AssistantMessage Result, IReadOnlyList<StreamEvent> Events, int HttpCount, int PayloadCount, JsonElement? Body);
}
