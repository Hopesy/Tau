// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Registry;
using Tau.Ai.Serialization;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Chat grammar 回归】验证声明、历史、交错输入与终态的公开协议行为。</summary>
public sealed class ChatCompletionsGrammarTests
{
    private const string Schema = """{"type":"object","properties":{"code":{"type":"string"}},"required":["code"]}""";
    private const string Input = "中文😀\nprint(\"x\")\t\\end";

    /// <summary>生成两种实现和六个公开入口的组合。</summary>
    /// <returns>协议和入口参数。</returns>
    public static IEnumerable<object[]> Entries()
    {
        foreach (var protocol in new[] { "chat", "compatible" })
            foreach (var entry in new[] { "direct", "direct-simple", "stream", "simple", "models", "models-simple" })
                yield return [protocol, entry];
    }

    /// <summary>【AI】【Chat grammar 回归】验证实际声明、JSON 增量及下一轮历史调用和结果。</summary>
    /// <param name="protocol">协议实现。</param>
    /// <param name="entry">公开入口。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [MemberData(nameof(Entries))]
    public async Task RoundTrip_AllEntries(string protocol, string entry)
    {
        var capture = await SendAsync(protocol, entry: entry, events:
        [Call("""{"index":3,"id":"call_1","type":"custom","custom":{"name":"execute","input":"中文"}}"""),
            Call("{\"index\":3,\"custom\":{\"input\":" + Quote(Input[2..]) + "}}"), Finish("tool_calls"),
            """{"choices":[],"usage":{"prompt_tokens":17,"completion_tokens":4}}""", "[DONE]"]);
        AssertGrammar(capture.Body!.Value.GetProperty("tools")[0], "lark");
        Assert.Equal(StopReason.ToolUse, capture.Result.StopReason);
        Assert.Equal(new Usage(17, 4), capture.Result.Usage);
        var call = Assert.Single(capture.Result.Content.OfType<ToolCallContent>());
        Assert.Equal("call_1", call.Id);
        Assert.Equal("execute", call.Name);
        Assert.Equal(Input, Json(call.Arguments).GetProperty("code").GetString());
        AssertDelta(capture, call, "code", Input);
        Assert.Equal(call, Assert.Single(capture.Events.OfType<ToolCallEndEvent>()).ToolCall);
        Assert.Single(capture.Events.OfType<DoneEvent>());

        var replay = await SendAsync(protocol, entry: entry, context: new(null,
            [new SystemMessage("base") { ToolsAdded = [Grammar()] }, new UserMessage("run"), capture.Result,
                new ToolResultMessage("call_1", [new TextContent("done")])], null));
        var messages = replay.Body!.Value.GetProperty("messages").EnumerateArray().ToArray();
        var assistant = Assert.Single(messages, item => item.GetProperty("role").GetString() == "assistant");
        var saved = Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("custom", saved.GetProperty("type").GetString());
        Assert.Equal(Input, saved.GetProperty("custom").GetProperty("input").GetString());
        var output = Assert.Single(messages, item => item.GetProperty("role").GetString() == "tool");
        Assert.Equal(saved.GetProperty("id").GetString(), output.GetProperty("tool_call_id").GetString());
        Assert.Equal("done", output.GetProperty("content").GetString());
    }

    /// <summary>验证原生工具新增与严格 JSON 工具共存，Grammar 使用嵌套格式。</summary>
    /// <param name="protocol">协议实现。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    public async Task Declarations_MixGrammarAndStrictAtBothLocations(string protocol)
    {
        var strict = Grammar() with { Name = "lookup", ConstrainedSampling = new() { Type = "json_schema", Strict = "require" } };
        var regex = Grammar() with { ConstrainedSampling = new() { Type = "grammar", Variants = new Dictionary<string, string> { ["openai_regex"] = ".*" } } };
        var capture = await SendAsync(protocol, context: new(null,
            [new SystemMessage("base") { ToolsAdded = [strict, Grammar()] }, new UserMessage("before"),
                new SystemMessage("update") { ToolsAdded = [regex with { Name = "later" }] }, new UserMessage("after")], null),
            compat: Enabled() with { SupportsStrictMode = true, SupportsMidConvoSystemMessages = true, SupportsMidConvoToolAdditions = true });
        var body = capture.Body!.Value;
        var top = body.GetProperty("tools");
        Assert.True(top[0].GetProperty("function").GetProperty("strict").GetBoolean());
        AssertGrammar(top[1], "lark");
        var addition = Assert.Single(body.GetProperty("messages").EnumerateArray(), item => item.TryGetProperty("tools", out _));
        AssertGrammar(addition.GetProperty("tools")[0], "regex");
        Assert.False(Grammar().ParameterSchema.TryGetProperty("additionalProperties", out _));
    }

    /// <summary>验证已移除工具仍按历史声明重放，能力关闭和同名重定义切回 function。</summary>
    /// <param name="protocol">协议实现。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    public async Task History_HandlesRemovalCapabilityAndRedefinition(string protocol)
    {
        var messages = new ChatMessage[]
        {
            new SystemMessage("base") { ToolsAdded = [Grammar()] }, new UserMessage("run"),
            new AssistantMessage { Content = [new ToolCallContent("call_old", "execute", """{"code":"old"}""")] },
            new ToolResultMessage("call_old", [new TextContent("result")]) { IsError = true },
            new SystemMessage("removed") { ToolsRemoved = [new("execute")] }, new UserMessage("continue")
        };
        var context = new LlmContext(null, messages, null);
        var enabled = await SendAsync(protocol, context: context);
        Assert.Empty(enabled.Body!.Value.GetProperty("tools").EnumerateArray());
        Assert.Equal("old", HistoryCall(enabled).GetProperty("custom").GetProperty("input").GetString());
        var disabled = await SendAsync(protocol, context: context, compat: Enabled() with { SupportsOpenAiGrammarTools = false });
        Assert.Equal("function", HistoryCall(disabled).GetProperty("type").GetString());
        var redefined = await SendAsync(protocol, context: context with
        {
            Messages = [.. messages, new SystemMessage("replace") { ToolsAdded = [Grammar() with { ConstrainedSampling = null }] }]
        });
        Assert.Equal("function", HistoryCall(redefined).GetProperty("type").GetString());
    }

    /// <summary>非法声明和非法历史输入必须在载荷回调及 HTTP 前失败；关闭能力仍可回退。</summary>
    /// <param name="protocol">协议实现。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    public async Task InvalidGrammar_RejectsBeforeTransport(string protocol)
    {
        var invalid = Grammar() with { ParameterSchema = Json("""{"type":"object","properties":{}}""") };
        var context = new LlmContext(null, [new SystemMessage("base") { ToolsAdded = [invalid] },
            new UserMessage("run"), new SystemMessage("remove") { ToolsRemoved = [new("execute")] }], null);
        var rejected = await SendAsync(protocol, context: context);
        AssertError(rejected, "execute");
        Assert.Null(rejected.Body);
        Assert.Equal(0, rejected.PayloadCount);
        var fallback = await SendAsync(protocol, context: new(null, [new UserMessage("run")], [invalid]),
            compat: Enabled() with { SupportsOpenAiGrammarTools = false });
        Assert.Equal("function", fallback.Body!.Value.GetProperty("tools")[0].GetProperty("type").GetString());

        var history = await SendAsync(protocol, context: new(null,
            [new AssistantMessage { Content = [new ToolCallContent("call_1", "execute", """{"code":42}""")] }], [Grammar()]));
        AssertError(history, "execute");
        Assert.Null(history.Body);
        Assert.Equal(0, history.PayloadCount);
    }

    /// <summary>并行 custom/function 调用按索引和 ID 绑定，文本交错不会提前闭合。</summary>
    /// <param name="protocol">协议实现。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    public async Task Stream_BindsInterleavedCallsAndLateIdentifiers(string protocol)
    {
        var capture = await SendAsync(protocol, events:
        [Call("""{"index":4}"""), Call("""{"index":4,"id":"a","custom":{"name":"execute","input":"A"}}"""),
            Call("""{"id":"b","custom":{"name":"execute","input":"B"}}"""),
            """{"choices":[{"delta":{"content":"working"}}]}""",
            Call("""{"id":"a","custom":{"input":"1"}}"""),
            Call("""{"index":9,"id":"b","custom":{"input":"2"}}"""),
            Call("""{"index":9,"custom":{"input":"3"}}"""),
            Call("""{"index":12,"function":{"arguments":"{\"n\":"}}"""),
            Call("""{"index":12,"id":"c","function":{"name":"count","arguments":"7}"}}"""), Finish("tool_calls")]);
        var calls = capture.Result.Content.OfType<ToolCallContent>().ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Equal("A1", Json(calls[0].Arguments).GetProperty("code").GetString());
        Assert.Equal("B23", Json(calls[1].Arguments).GetProperty("code").GetString());
        Assert.Equal("c", calls[2].Id);
        Assert.Equal("count", calls[2].Name);
        Assert.Equal(7, Json(calls[2].Arguments).GetProperty("n").GetInt32());
        AssertDelta(capture, calls[0], "code", "A1");
        AssertDelta(capture, calls[1], "code", "B23");
        Assert.Equal(3, capture.Events.OfType<ToolCallStartEvent>().Count());
        Assert.Equal(3, capture.Events.OfType<ToolCallEndEvent>().Count());
        Assert.Equal(new[] { 0, 1, 2, 3 }, capture.Events.Where(item => item is ToolCallEndEvent or TextEndEvent)
            .Select(item => item is ToolCallEndEvent toolEnd ? toolEnd.ContentIndex : ((TextEndEvent)item).ContentIndex));
        var firstEnd = capture.Events.ToList().FindIndex(item => item is ToolCallEndEvent);
        Assert.DoesNotContain(capture.Events.Skip(firstEnd), item => item is ToolCallDeltaEvent delta && delta.Delta.Contains('7'));
        Assert.Equal(Assert.Single(capture.Events.OfType<TextStartEvent>()).ContentIndex,
            Assert.Single(capture.Events.OfType<TextDeltaEvent>()).ContentIndex);
    }

    /// <summary>空字符串、未知工具和特殊属性名仍能形成完整 JSON 参数及闭合增量。</summary>
    /// <param name="protocol">协议实现。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    public async Task Stream_HandlesEmptyUnknownAndEscapedProperty(string protocol)
    {
        var weird = Grammar() with { ParameterSchema = Json("""{"type":"object","properties":{"x\"\n":{"type":"string"}},"required":["x\"\n"]}""") };
        var capture = await SendAsync(protocol, context: new(null, [new UserMessage("run")], [weird]), events:
        [Call("""{"index":0,"id":"a","custom":{"name":"execute","input":""}}"""),
            Call("""{"index":1,"id":"b","custom":{"name":"unknown","input":"raw"}}"""), Finish("tool_calls")]);
        var calls = capture.Result.Content.OfType<ToolCallContent>().ToArray();
        AssertDelta(capture, calls[0], "x\"\n", "");
        AssertDelta(capture, calls[1], "input", "raw");
    }

    /// <summary>结束原因约定控制缺失终态的回退，截断不会被改成 ToolUse。</summary>
    /// <param name="protocol">协议实现。</param>
    /// <param name="finish">供应商结束原因。</param>
    /// <param name="requiresFinish">是否要求结束原因。</param>
    /// <param name="expected">预期终态。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("chat", null, true, StopReason.Error)]
    [InlineData("compatible", null, true, StopReason.Error)]
    [InlineData("chat", null, false, StopReason.ToolUse)]
    [InlineData("compatible", null, false, StopReason.ToolUse)]
    [InlineData("chat", "length", true, StopReason.MaxTokens)]
    [InlineData("compatible", "length", true, StopReason.MaxTokens)]
    [InlineData("chat", "content_filter", true, StopReason.Error)]
    [InlineData("compatible", "content_filter", true, StopReason.Error)]
    public async Task Stream_PreservesTerminationPolicy(string protocol, string? finish, bool requiresFinish, StopReason expected)
    {
        var chunks = new List<string> { Call("""{"index":0,"id":"a","custom":{"name":"execute","input":"partial"}}""") };
        if (finish is not null) chunks.Add(Finish(finish));
        chunks.Add("[DONE]");
        var capture = await SendAsync(protocol, compat: Enabled() with { SupportsFinishReason = requiresFinish }, events: chunks.ToArray());
        Assert.Equal(expected, capture.Result.StopReason);
        Assert.Equal("partial", Json(Assert.Single(capture.Result.Content.OfType<ToolCallContent>()).Arguments).GetProperty("code").GetString());
        if (expected == StopReason.Error) AssertError(capture, finish ?? "without finish_reason");
    }

    /// <summary>供应商错误或损坏事件保留已累积参数和模型身份，不发布成功终态。</summary>
    /// <param name="protocol">协议实现。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    public async Task Stream_PreservesPartialOnFailure(string protocol)
    {
        foreach (var failure in new[] { """{"error":{"message":"provider failure"}}""", "not-json" })
        {
            var capture = await SendAsync(protocol, events:
                [Call("""{"index":0,"id":"a","custom":{"name":"execute","input":"partial"}}"""), failure]);
            AssertError(capture, failure.StartsWith('{') ? "provider failure" : "invalid");
            Assert.Equal("grammar-model", capture.Result.Model);
            Assert.Equal("grammar-test", capture.Result.Provider);
            Assert.Equal("partial", Json(Assert.Single(capture.Result.Content.OfType<ToolCallContent>()).Arguments).GetProperty("code").GetString());
            Assert.Empty(capture.Events.OfType<ToolCallEndEvent>());
        }
    }

    /// <summary>断言声明具有 Chat Completions 专用 grammar 层级且不混入 function 字段。</summary>
    /// <param name="tool">协议工具声明。</param>
    /// <param name="syntax">预期语法类型。</param>
    private static void AssertGrammar(JsonElement tool, string syntax)
    {
        Assert.Equal("custom", tool.GetProperty("type").GetString());
        Assert.False(tool.TryGetProperty("function", out _));
        var custom = tool.GetProperty("custom");
        Assert.False(custom.TryGetProperty("strict", out _));
        Assert.False(custom.TryGetProperty("parameters", out _));
        Assert.Equal("grammar", custom.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(syntax, custom.GetProperty("format").GetProperty("grammar").GetProperty("syntax").GetString());
        Assert.Equal(syntax == "lark" ? "start: WORD" : ".*", custom.GetProperty("format").GetProperty("grammar").GetProperty("definition").GetString());
    }

    /// <summary>断言所有参数增量拼接后是与最终调用一致的 JSON。</summary>
    /// <param name="capture">响应快照。</param>
    /// <param name="call">最终工具调用。</param>
    /// <param name="property">输入属性名。</param>
    /// <param name="value">输入文本。</param>
    private static void AssertDelta(Capture capture, ToolCallContent call, string property, string value)
    {
        var index = capture.Result.Content.ToList().IndexOf(call);
        var text = string.Concat(capture.Events.OfType<ToolCallDeltaEvent>().Where(item => item.ContentIndex == index).Select(item => item.Delta));
        Assert.Equal(value, Json(text).GetProperty(property).GetString());
        Assert.Equal(value, Json(call.Arguments).GetProperty(property).GetString());
    }

    /// <summary>断言单一错误终态及关键错误文本。</summary>
    /// <param name="capture">响应快照。</param>
    /// <param name="reason">关键错误文本。</param>
    private static void AssertError(Capture capture, string reason)
    {
        Assert.Equal(StopReason.Error, capture.Result.StopReason);
        Assert.Contains(reason, capture.Result.ErrorMessage);
        Assert.Single(capture.Events.OfType<ErrorEvent>());
        Assert.Empty(capture.Events.OfType<DoneEvent>());
    }

    /// <summary>取出历史助手的唯一工具调用。</summary>
    /// <param name="capture">请求快照。</param>
    /// <returns>协议调用对象。</returns>
    private static JsonElement HistoryCall(Capture capture) => Assert.Single(capture.Body!.Value.GetProperty("messages").EnumerateArray(),
        item => item.GetProperty("role").GetString() == "assistant").GetProperty("tool_calls")[0];

    /// <summary>创建启用语法工具及结束原因检查的模型能力。</summary>
    /// <returns>测试能力配置。</returns>
    private static ModelCompatibility Enabled() => new() { SupportsOpenAiGrammarTools = true, SupportsFinishReason = true };

    /// <summary>创建具有唯一字符串输入的工具，逆序变体验证 Lark 优先级。</summary>
    /// <returns>语法工具。</returns>
    private static Tool Grammar() => new("execute", "Execute code", Json(Schema))
    {
        ConstrainedSampling = new() { Type = "grammar", Variants = new Dictionary<string, string> { ["openai_regex"] = ".*", ["openai_lark"] = "start: WORD" } }
    };

    /// <summary>生成单个调用的标准响应块。</summary>
    /// <param name="tool">工具增量 JSON。</param>
    /// <returns>供应商响应 JSON。</returns>
    private static string Call(string tool) => "{\"id\":\"completion-1\",\"choices\":[{\"delta\":{\"tool_calls\":[" + tool + "]}}]}";

    /// <summary>生成结束原因响应块。</summary>
    /// <param name="reason">结束原因。</param>
    /// <returns>供应商响应 JSON。</returns>
    private static string Finish(string reason) => "{\"choices\":[{\"delta\":{},\"finish_reason\":" + Quote(reason) + "}]}";

    /// <summary>通过源生成序列化生成字符串 JSON，避免反射依赖。</summary>
    /// <param name="value">原始字符串。</param>
    /// <returns>带引号的 JSON 字符串。</returns>
    private static string Quote(string value) => JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["value"] = value },
        TauAiJsonContext.Default.DictionaryStringString).GetProperty("value").GetRawText();

    /// <summary>解析独立持有内存的 JSON。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>JSON 副本。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    /// <summary>【AI】【Chat grammar 回归】执行公开入口并捕获请求、事件及载荷回调次数。</summary>
    /// <param name="protocol">协议实现。</param>
    /// <param name="context">可选请求上下文。</param>
    /// <param name="compat">可选模型能力。</param>
    /// <param name="entry">公开调用入口。</param>
    /// <param name="events">模拟供应商响应块。</param>
    /// <returns>执行快照。</returns>
    private static async Task<Capture> SendAsync(string protocol, LlmContext? context = null, ModelCompatibility? compat = null,
        string entry = "direct", string[]? events = null)
    {
        events ??= [Finish("stop")];
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(string.Concat(events.Select(item => "data: " + item + "\n\n"))));
        using var client = new HttpClient(handler);
        IStreamProvider provider = protocol == "chat" ? new OpenAiProvider(client) : new OpenAiCompatibleProvider("grammar-compatible", "https://example.invalid/v1", httpClient: client);
        var model = new Model { Id = "grammar-model", Name = "Grammar", Provider = "grammar-test", Api = provider.Api, BaseUrl = "https://example.invalid/v1", Compat = compat ?? Enabled() };
        var payloadCount = 0;
        var options = new SimpleStreamOptions { ApiKey = "test-key", OnPayload = (_, _) => { payloadCount++; return ValueTask.FromResult<object?>(null); } };
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var models = new Models([new ProviderDefinition(model.Provider, provider, [model])]);
        var actualContext = context ?? new("base", [new UserMessage("run")], [Grammar()]);
        var stream = entry switch
        {
            "direct-simple" => provider.StreamSimple(model, actualContext, options),
            "stream" => StreamFunctions.Stream(registry, model, actualContext, options, new([])),
            "simple" => StreamFunctions.StreamSimple(registry, model, actualContext, options, new([])),
            "models" => models.Stream(model, actualContext, options),
            "models-simple" => models.StreamSimple(model, actualContext, options),
            _ => provider.Stream(model, actualContext, options)
        };
        var collected = new List<StreamEvent>();
        await foreach (var item in stream) collected.Add(item);
        return new(await stream.ResultAsync, collected, handler.Requests.Count == 0 ? null : Json(handler.CapturedBody), payloadCount);
    }

    private sealed record Capture(AssistantMessage Result, IReadOnlyList<StreamEvent> Events, JsonElement? Body, int PayloadCount);
}
