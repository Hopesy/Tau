// 作者：xxx
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Registry;
using Tau.Ai.Serialization;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Responses 语法回归】通过 HTTP 和 WebSocket 验证声明、重放与工具事件闭环。</summary>
public sealed partial class ResponsesGrammarTests
{
    private static readonly string[] Protocols = ["responses", "azure", "codex", "codex-ws"];
    private const string Terminal = """{"type":"response.completed","response":{"id":"r","status":"completed"}}""";

    /// <summary>生成四条传输路径与六个公开入口的组合。</summary>
    /// <returns>入口组合。</returns>
    public static IEnumerable<object[]> EntryCases()
    {
        foreach (var protocol in Protocols)
            foreach (var entry in new[] { "direct", "direct-simple", "stream", "simple", "models", "models-simple" })
                yield return [protocol, entry];
    }

    /// <summary>公开入口把 Lark 工具声明与响应文本转换为统一工具调用，再重放调用与执行结果。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <param name="entry">请求入口。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [MemberData(nameof(EntryCases))]
    public async Task RoundTrip_DeclaresStreamsAndReplays(string protocol, string entry)
    {
        var tool = GrammarTool();
        var context = new LlmContext("base", [new UserMessage("run")], [tool]);
        var capture = await SendAsync(protocol, context, Enabled(), entry, CustomEvents("print('中文😀')"));
        var body = Body(capture);
        var definition = Assert.Single(body.GetProperty("tools").EnumerateArray());
        Assert.Equal("custom", definition.GetProperty("type").GetString());
        Assert.Equal("grammar", definition.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("lark", definition.GetProperty("format").GetProperty("syntax").GetString());
        Assert.False(definition.TryGetProperty("parameters", out _));
        Assert.False(definition.TryGetProperty("strict", out _));
        Assert.Equal(StopReason.ToolUse, capture.Result.StopReason);
        var call = Assert.IsType<ToolCallContent>(Assert.Single(capture.Result.Content));
        Assert.Equal("call_1|ctc_1", call.Id);
        Assert.Equal("print('中文😀')", Json(call.Arguments).GetProperty("code").GetString());
        Assert.Equal(call, Assert.Single(capture.Events.OfType<ToolCallEndEvent>()).ToolCall);
        var deltaJson = string.Concat(capture.Events.OfType<ToolCallDeltaEvent>().Select(item => item.Delta));
        Assert.True(JsonElement.DeepEquals(Json(call.Arguments), Json(deltaJson)));

        // 1. 【AI】【语法回归】把结果送回下一轮，缺省 ToolName 也可通过调用标识恢复配对
        var nextContext = context with { Messages = [.. context.Messages, capture.Result, new ToolResultMessage(call.Id, [new TextContent("done")])] };
        var next = Body(await SendAsync(protocol, nextContext, Enabled(), entry));
        var input = next.GetProperty("input").EnumerateArray().ToArray();
        var replay = Assert.Single(input, item => Type(item) == "custom_tool_call");
        Assert.Equal("ctc_1", replay.GetProperty("id").GetString());
        Assert.Equal("print('中文😀')", replay.GetProperty("input").GetString());
        var result = Assert.Single(input, item => Type(item) == "custom_tool_call_output");
        Assert.Equal("call_1", result.GetProperty("call_id").GetString());
        Assert.Equal("start: WORD", tool.ConstrainedSampling!.Variants!["openai_lark"]);
    }

    /// <summary>regex 声明、原生新增和工具搜索共用语法转换，JSON 工具仍独立启用 strict。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task Declarations_HandleInlineAndToolSearch(string protocol)
    {
        var jsonTool = GrammarTool() with { Name = "json", ConstrainedSampling = new() { Type = "json_schema", Strict = "require" } };
        var regex = GrammarTool() with { ConstrainedSampling = new() { Type = "grammar", Variants = new Dictionary<string, string> { ["openai_regex"] = "[0-9]+" } } };
        var context = new LlmContext(null, [new SystemMessage("base") { ToolsAdded = [jsonTool] }, new UserMessage("before"),
            new SystemMessage("update") { ToolsAdded = [regex] }, new UserMessage("after")], null);
        foreach (var search in new[] { false, true })
        {
            var body = Body(await SendAsync(protocol, context, Enabled() with
            {
                SupportsStrictMode = true, SupportsMidConvoSystemMessages = true, SupportsAdditionalTools = !search, SupportsToolSearch = search
            }));
            Assert.True(body.GetProperty("tools")[0].GetProperty("strict").GetBoolean());
            var addition = Assert.Single(body.GetProperty("input").EnumerateArray(), item => Type(item) == (search ? "tool_search_output" : "additional_tools"));
            var custom = addition.GetProperty("tools")[0];
            Assert.Equal("custom", Type(custom));
            Assert.Equal("regex", custom.GetProperty("format").GetProperty("syntax").GetString());
            Assert.Equal(search, custom.TryGetProperty("defer_loading", out _));
        }
    }

    /// <summary>移除工具后历史 grammar 调用仍能重放，声明折叠不能丢失输入属性。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task History_RetainsRemovedDeclaration(string protocol)
    {
        var model = Model(protocol);
        var context = new LlmContext(null, [new SystemMessage("base") { ToolsAdded = [GrammarTool()] }, new UserMessage("run"),
            History(model), new ToolResultMessage("call_1|ctc_1", [new TextContent("failed")], true) { ToolName = "execute" },
            new SystemMessage("") { ToolsRemoved = [new("execute")] }, new UserMessage("next")], null);
        var body = Body(await SendAsync(protocol, context, Enabled()));
        Assert.False(body.TryGetProperty("tools", out _));
        Assert.Single(body.GetProperty("input").EnumerateArray(), item => Type(item) == "custom_tool_call");
        var result = Assert.Single(body.GetProperty("input").EnumerateArray(), item => Type(item) == "custom_tool_call_output");
        Assert.False(result.TryGetProperty("status", out _));
        Assert.Single(Transcript.GetDeclaredTools(context.Messages));
        Assert.Empty(Transcript.GetCurrentTools(context.Messages));
    }

    /// <summary>能力或工具类型变化时切换调用格式并丢弃不匹配或跨模型的输出项 id。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task Replay_SwitchesCapabilityAndDropsForeignItemIds(string protocol)
    {
        var model = Model(protocol);
        var context = new LlmContext(null, [History(model), new UserMessage("next")], [GrammarTool()]);
        var disabled = Body(await SendAsync(protocol, context, new() { SupportsOpenAiGrammarTools = false }));
        Assert.Equal("function", Type(disabled.GetProperty("tools")[0]));
        var function = Assert.Single(disabled.GetProperty("input").EnumerateArray(), item => Type(item) == "function_call");
        Assert.False(function.TryGetProperty("id", out _));
        Assert.Equal("print(1)", Json(function.GetProperty("arguments").GetString()!).GetProperty("code").GetString());
        Assert.Single(disabled.GetProperty("input").EnumerateArray(), item => Type(item) == "function_call_output");
        foreach (var assistant in new[]
        {
            History(model) with { Model = "other" }, History(model) with { Provider = "other" },
            History(model) with { Api = "anthropic-messages" },
            History(model) with { Content = [new ToolCallContent("call_1|fc_1", "execute", "{\"code\":\"print(1)\"}")] }
        })
        {
            var body = Body(await SendAsync(protocol, context with { Messages = [assistant, new UserMessage("next")] }, Enabled()));
            var custom = Assert.Single(body.GetProperty("input").EnumerateArray(), item => Type(item) == "custom_tool_call");
            Assert.False(custom.TryGetProperty("id", out _));
            Assert.Equal("call_1", custom.GetProperty("call_id").GetString());
            Assert.Single(body.GetProperty("input").EnumerateArray(), item => Type(item) == "custom_tool_call_output");
        }
        var plain = GrammarTool() with { ConstrainedSampling = null };
        var redefined = context with { Messages = [new SystemMessage("") { ToolsAdded = [plain] }, .. context.Messages] };
        Assert.Single(Body(await SendAsync(protocol, redefined, Enabled())).GetProperty("input").EnumerateArray(), item => Type(item) == "function_call");
    }

    /// <summary>错误 grammar 和错误历史参数在载荷回调、HTTP 与 WebSocket 连接之前失败。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task InvalidGrammar_FailsBeforeTransport(string protocol)
    {
        var bad = GrammarTool() with { ParameterSchema = Json("{\"type\":\"object\"}") };
        var context = new LlmContext(null, [new UserMessage("hi")], [bad]);
        var capture = await SendAsync(protocol, context, Enabled());
        AssertFailure(capture, "Tool \"execute\" cannot use grammar");
        Assert.Equal(0, capture.PayloadCount);
        Assert.Null(capture.Body);
        Assert.Equal(0, capture.ConnectCount);
        Body(await SendAsync(protocol, context, new()));
        context = new(null, [new SystemMessage("") { ToolsAdded = [bad] }, new SystemMessage("") { ToolsRemoved = [new("execute")] }, new UserMessage("hi")], null);
        AssertFailure(await SendAsync(protocol, context, Enabled()), "exactly one required string property");
        context = new(null, [History(Model(protocol)) with { Content = [new ToolCallContent("call_1|ctc_1", "execute", "{\"code\":42}")] }], [GrammarTool()]);
        AssertFailure(await SendAsync(protocol, context, Enabled()), "requires argument \"code\" to be a string");
    }

    /// <summary>配置可读取上游大写 AI 字段和 Tau 旧拼写，并保持模型级禁用和存储往返。</summary>
    /// <param name="field">配置字段拼写。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("supportsOpenAIGrammarTools")]
    [InlineData("supportsOpenAiGrammarTools")]
    public async Task Configuration_LoadsGrammarCapability(string field)
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-grammar-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"providers\":{\"openai\":{\"compat\":{\"" + field + "\":true},\"options\":{\"toolChoice\":\"auto\"},\"models\":[{\"id\":\"enabled\"},{\"id\":\"disabled\",\"compat\":{\"" + field + "\":false}}]}}}");
            var store = new ModelConfigurationStore([path]);
            var models = store.ApplyToModels([], ModelTypes.Chat);
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(models.Single(model => model.Id == "enabled"), TauAiJsonContext.Default.Model), TauAiJsonContext.Default.Model)!;
            Assert.True(restored.Compat!.SupportsOpenAiGrammarTools);
            var context = new LlmContext(null, [new UserMessage("run")], [GrammarTool()]);
            Assert.Equal("custom", Type(Body(await SendAsync("responses", context, restored.Compat, "simple", configuration: store)).GetProperty("tools")[0]));
            var disabled = models.Single(model => model.Id == "disabled");
            Assert.False(disabled.Compat!.SupportsOpenAiGrammarTools);
            Assert.Equal("function", Type(Body(await SendAsync("responses", context, disabled.Compat, "simple", configuration: store)).GetProperty("tools")[0]));
        }
        finally { File.Delete(path); }
    }

    /// <summary>并行普通函数和语法调用按 output_index 绑定，乱序增量不能污染其他调用。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task Stream_SeparatesInterleavedSlots(string protocol)
    {
        string[] events = [
            Item("added", 4, "ctc_a", "a", "execute", "A"),
            Item("added", 9, "ctc_b", "b", "second", ""),
            """{"type":"response.output_item.added","output_index":12,"item":{"id":"fc_c","type":"function_call","call_id":"c","name":"json","arguments":""}}""",
            Delta(9, "B"), Delta(4, "1"), Delta(404, "WRONG"),
            """{"type":"response.custom_tool_call_input.delta","delta":"UNBOUND"}""",
            """{"type":"response.function_call_arguments.delta","output_index":4,"delta":"WRONG"}""",
            """{"type":"response.function_call_arguments.delta","output_index":12,"delta":"{\"x\":"}""",
            Delta(9, "2"),
            """{"type":"response.function_call_arguments.done","output_index":12,"arguments":"{\"x\":3}"}""",
            Item("done", 9, "ctc_b", "b", "second", "B2"), Item("done", 4, "ctc_a", "a", "execute", "A1"),
            """{"type":"response.output_item.done","output_index":12,"item":{"id":"fc_c","type":"function_call","call_id":"c","name":"json","arguments":"{\"x\":3}"}}""", Terminal];
        var context = new LlmContext(null, [new UserMessage("run")], [GrammarTool(), GrammarTool("payload") with { Name = "second" }]);
        var capture = await SendAsync(protocol, context, Enabled(), events: events);
        Assert.Equal(StopReason.ToolUse, capture.Result.StopReason);
        var calls = capture.Result.Content.OfType<ToolCallContent>().ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Equal("A1", Json(calls[0].Arguments).GetProperty("code").GetString());
        Assert.Equal("B2", Json(calls[1].Arguments).GetProperty("payload").GetString());
        Assert.Equal(3, Json(calls[2].Arguments).GetProperty("x").GetInt32());
        foreach (var group in capture.Events.OfType<ToolCallDeltaEvent>().GroupBy(item => item.ContentIndex))
            Assert.True(JsonElement.DeepEquals(Json(calls[group.Key].Arguments), Json(string.Concat(group.Select(item => item.Delta)))));
        Assert.Equal(3, capture.Events.OfType<ToolCallEndEvent>().Count());
    }

    /// <summary>仅有输出项完成事件也能还原空输入或正文，重复完成和迟到增量不产生第二次结束。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task Stream_HandlesDoneOnlyAndDuplicates(string protocol)
    {
        foreach (var input in new[] { "", "中文\n\"\\😀" })
        {
            var done = Item("done", 7, "ctc_1", "call_1", "unknown", input);
            var capture = await SendAsync(protocol, events: [done, done, Delta(7, "late"), Terminal]);
            var call = Assert.IsType<ToolCallContent>(Assert.Single(capture.Result.Content));
            Assert.Equal(input, Json(call.Arguments).GetProperty("input").GetString());
            Assert.Single(capture.Events.OfType<ToolCallStartEvent>());
            Assert.Single(capture.Events.OfType<ToolCallEndEvent>());
            Assert.Equal(input, Json(string.Concat(capture.Events.OfType<ToolCallDeltaEvent>().Select(item => item.Delta))).GetProperty("input").GetString());
        }
    }

    /// <summary>缺少输出项完成、非单调输入和无终态断流都不能交付工具执行，并保留部分响应。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task Stream_RejectsUnfinishedAndInvalidInput(string protocol)
    {
        var start = Item("added", 0, "ctc_1", "call_1", "execute", "");
        var context = new LlmContext(null, [new UserMessage("run")], [GrammarTool()]);
        var unfinished = await SendAsync(protocol, context, Enabled(), events: [start, Delta(0, "abc"), InputDone(0, "abc"), Terminal]);
        AssertFailure(unfinished, "unfinished tool call");
        Assert.Single(unfinished.Result.Content);
        Assert.Empty(unfinished.Events.OfType<ToolCallEndEvent>());
        var invalid = await SendAsync(protocol, context, Enabled(), events: [start, Delta(0, "abc"), InputDone(0, "a"), Terminal]);
        AssertFailure(invalid, "changed non-monotonically");
        Assert.Equal("abc", Json(Assert.IsType<ToolCallContent>(Assert.Single(invalid.Result.Content)).Arguments).GetProperty("code").GetString());
        AssertFailure(await SendAsync(protocol, context, Enabled(), events: [start, Delta(0, "abc")]), "before a terminal response event");
        AssertFailure(await SendAsync(protocol, context, Enabled(), events: [start, InputDone(0, "abc"), Delta(0, "d"), Terminal]), "changed after it was closed");
        AssertFailure(await SendAsync(protocol, context, Enabled(), events: [start, Delta(0, "abc"),
            """{"type":"response.output_item.done","output_index":0,"item_id":"ctc_1"}""", Terminal]), "unfinished tool call");
        AssertFailure(await SendAsync(protocol, context, Enabled(), events: [start, Delta(0, "abc"),
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"ctc_1","arguments":"{}"}}""", Terminal]), "unfinished tool call");
    }

    /// <summary>不完整响应保留截断状态，不能因包含工具块而错误变成 ToolUse。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task Stream_PreservesIncompleteStatus(string protocol)
    {
        var capture = await SendAsync(protocol, events: [Item("added", 0, "ctc_1", "call_1", "unknown", "partial"),
            """{"type":"response.incomplete","response":{"id":"r","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}}"""]);
        Assert.Equal(StopReason.MaxTokens, capture.Result.StopReason);
        Assert.Single(capture.Result.Content);
    }

    /// <summary>供应商报错保留原始错误与部分语法输入，WebSocket 连接层不能覆盖其原因。</summary>
    /// <param name="protocol">传输路径。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("codex")]
    [InlineData("codex-ws")]
    public async Task Stream_PreservesProviderFailure(string protocol)
    {
        foreach (var error in new[]
        {
            """{"type":"response.failed","response":{"status":"failed","error":{"message":"provider failure"}}}""",
            """{"type":"error","error":{"message":"provider failure"}}"""
        })
        {
            var capture = await SendAsync(protocol, events: [Item("added", 0, "ctc_1", "call_1", "unknown", "partial"), error]);
            AssertFailure(capture, "provider failure");
            Assert.Equal("partial", Json(Assert.IsType<ToolCallContent>(Assert.Single(capture.Result.Content)).Arguments).GetProperty("input").GetString());
        }
    }

    /// <summary>创建包含初始文本、增量、输入完成与输出项完成的事件序列。</summary>
    /// <param name="input">最终输入文本。</param>
    /// <returns>语法调用事件序列。</returns>
    private static string[] CustomEvents(string input) => [Item("added", 0, "ctc_1", "call_1", "execute", input[..2]),
        Delta(0, input[2..]), InputDone(0, input), Item("done", 0, "ctc_1", "call_1", "execute", input), Terminal];

    /// <summary>构造一个语法输出项事件。</summary>
    /// <param name="stage">added 或 done。</param>
    /// <param name="index">输出槽位。</param>
    /// <param name="id">输出项标识。</param>
    /// <param name="callId">调用标识。</param>
    /// <param name="name">工具名。</param>
    /// <param name="input">原始输入。</param>
    /// <returns>JSON 事件。</returns>
    private static string Item(string stage, int index, string id, string callId, string name, string input) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "response.output_item." + stage, ["output_index"] = index,
            ["item"] = new Dictionary<string, object> { ["type"] = "custom_tool_call", ["id"] = id, ["call_id"] = callId, ["name"] = name, ["input"] = input }
        }, OpenAiResponsesJsonContext.Default.DictionaryStringObject);

    /// <summary>构造语法输入增量。</summary>
    /// <param name="index">输出槽位。</param>
    /// <param name="delta">新增文本。</param>
    /// <returns>JSON 事件。</returns>
    private static string Delta(int index, string delta) => JsonSerializer.Serialize(new Dictionary<string, object>
    { ["type"] = "response.custom_tool_call_input.delta", ["output_index"] = index, ["delta"] = delta }, OpenAiResponsesJsonContext.Default.DictionaryStringObject);

    /// <summary>构造语法输入完成事件。</summary>
    /// <param name="index">输出槽位。</param>
    /// <param name="input">完整文本。</param>
    /// <returns>JSON 事件。</returns>
    private static string InputDone(int index, string input) => JsonSerializer.Serialize(new Dictionary<string, object>
    { ["type"] = "response.custom_tool_call_input.done", ["output_index"] = index, ["input"] = input }, OpenAiResponsesJsonContext.Default.DictionaryStringObject);

    /// <summary>创建有效的单字符串 grammar 工具。</summary>
    /// <param name="property">输入属性名。</param>
    /// <returns>语法工具。</returns>
    private static Tool GrammarTool(string property = "code") => new("execute", "Execute code", Json("{\"type\":\"object\",\"properties\":{\"" + property + "\":{\"type\":\"string\"}},\"required\":[\"" + property + "\"]}"))
    {
        ConstrainedSampling = new() { Type = "grammar", Variants = new Dictionary<string, string> { ["openai_lark"] = "start: WORD" } }
    };

    /// <summary>创建模型匹配的历史语法调用。</summary>
    /// <param name="model">原请求模型。</param>
    /// <returns>助手历史。</returns>
    private static AssistantMessage History(Model model) => new([new ToolCallContent("call_1|ctc_1", "execute", "{\"code\":\"print(1)\"}")])
    { Api = model.Api, Provider = model.Provider, Model = model.Id };

    /// <summary>创建语法能力配置。</summary>
    /// <returns>显式启用语法的能力。</returns>
    private static ModelCompatibility Enabled() => new() { SupportsOpenAiGrammarTools = true };

    /// <summary>读取协议项类型。</summary>
    /// <param name="item">JSON 项。</param>
    /// <returns>类型或空值。</returns>
    private static string? Type(JsonElement item) => item.TryGetProperty("type", out var type) ? type.GetString() : null;

    /// <summary>解析并复制 JSON。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>独立 JSON。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    /// <summary>确认成功后读取请求报文。</summary>
    /// <param name="capture">执行快照。</param>
    /// <returns>请求体或 WebSocket 帧。</returns>
    private static JsonElement Body(Capture capture)
    {
        Assert.True(capture.Result.StopReason != StopReason.Error, capture.Result.ErrorMessage);
        return capture.Body!.Value;
    }

    /// <summary>验证唯一错误终态，不允许成功 Done 事件。</summary>
    /// <param name="capture">执行快照。</param>
    /// <param name="reason">预期原因。</param>
    private static void AssertFailure(Capture capture, string reason)
    {
        Assert.Equal(StopReason.Error, capture.Result.StopReason);
        Assert.Contains(reason, capture.Result.ErrorMessage);
        Assert.Single(capture.Events.OfType<ErrorEvent>());
        Assert.Empty(capture.Events.OfType<DoneEvent>());
    }

    /// <summary>创建实际协议模型，供应商名称用于验证历史 item id 保留规则。</summary>
    /// <param name="protocol">协议别名。</param>
    /// <returns>测试模型。</returns>
    private static Model Model(string protocol) => new()
    {
        Id = "enabled", Name = "Grammar", BaseUrl = "https://example.invalid/v1",
        Api = protocol switch { "azure" => "azure-openai-responses", "codex" or "codex-ws" => "openai-codex-responses", _ => "openai-responses" },
        Provider = protocol switch { "azure" => "azure-openai-responses", "codex" or "codex-ws" => "openai-codex", _ => "openai" }
    };

    /// <summary>【AI】【语法传输回归】执行实际 Provider，收集请求及完整流事件。</summary>
    /// <param name="protocol">HTTP 或 WebSocket 路径。</param>
    /// <param name="context">可选上下文。</param>
    /// <param name="compat">可选能力。</param>
    /// <param name="entry">公开入口。</param>
    /// <param name="events">供应商事件。</param>
    /// <param name="configuration">可选配置。</param>
    /// <returns>执行快照。</returns>
    private static async Task<Capture> SendAsync(string protocol, LlmContext? context = null, ModelCompatibility? compat = null,
        string entry = "direct", string[]? events = null, ModelConfigurationStore? configuration = null)
    {
        events ??= [Terminal];
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(string.Concat(events.Select(item => "data: " + item + "\n\n"))));
        using var client = new HttpClient(handler);
        var socket = new GrammarSocket(events);
        IStreamProvider provider = protocol switch
        {
            "azure" => new AzureOpenAiResponsesProvider(client),
            "codex" or "codex-ws" => new OpenAiCodexResponsesProvider(client, socket),
            _ => new OpenAiResponsesProvider(client)
        };
        using var lifetime = provider as IDisposable;
        var model = Model(protocol) with { Compat = compat };
        var payloadCount = 0;
        var options = new SimpleStreamOptions
        {
            ApiKey = protocol.StartsWith("codex", StringComparison.Ordinal) ? OpenAiResponsesSharedTests.BuildFakeJwt("grammar") : "key",
            Transport = protocol == "codex-ws" ? StreamTransport.WebSocket : StreamTransport.Sse,
            OnPayload = (_, _) => { payloadCount++; return ValueTask.FromResult<object?>(null); }
        };
        StreamOptions advanced = protocol == "azure" ? new AzureOpenAiResponsesOptions { ApiKey = options.ApiKey, OnPayload = options.OnPayload } : options;
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var models = new Models([new ProviderDefinition(model.Provider, provider, [model])]);
        var actualContext = context ?? new LlmContext(null, [new UserMessage("run")], null);
        var stream = entry switch
        {
            "direct-simple" => provider.StreamSimple(model, actualContext, options),
            "stream" => StreamFunctions.Stream(registry, model, actualContext, advanced, configuration ?? new([])),
            "simple" => StreamFunctions.StreamSimple(registry, model, actualContext, options, configuration ?? new([])),
            "models" => models.Stream(model, actualContext, advanced),
            "models-simple" => models.StreamSimple(model, actualContext, options),
            _ => provider.Stream(model, actualContext, advanced)
        };
        var collected = new List<StreamEvent>();
        await foreach (var item in stream) collected.Add(item);
        var body = socket.Frames.SingleOrDefault() ?? (handler.Requests.Count > 0 ? handler.CapturedBody : null);
        return new(await stream.ResultAsync, collected, body is null ? null : Json(body), payloadCount, socket.ConnectCount);
    }

    private sealed record Capture(AssistantMessage Result, IReadOnlyList<StreamEvent> Events, JsonElement? Body, int PayloadCount, int ConnectCount);

    /// <summary>内存 WebSocket，记录连接和请求帧并提供预设事件。</summary>
    /// <param name="events">供应商事件。</param>
    private sealed class GrammarSocket(string[] events) : ICodexWebSocketTransport, ICodexWebSocketConnection
    {
        public WebSocketState State { get; private set; } = WebSocketState.Open;
        public int ConnectCount { get; private set; }
        public List<string> Frames { get; } = [];

        /// <summary>记录连接并返回内存连接。</summary>
        /// <param name="url">请求地址。</param>
        /// <param name="headers">认证和请求头。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>内存连接。</returns>
        public Task<ICodexWebSocketConnection> ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
        { ConnectCount++; return Task.FromResult<ICodexWebSocketConnection>(this); }

        /// <summary>记录请求帧。</summary>
        /// <param name="text">请求 JSON。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>已完成任务。</returns>
        public Task SendTextAsync(string text, CancellationToken cancellationToken = default) { Frames.Add(text); return Task.CompletedTask; }

        /// <summary>逐条返回预设事件，允许真实异步消费。</summary>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>JSON 事件序列。</returns>
        public async IAsyncEnumerable<string> ReadTextMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in events) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); }
        }

        /// <summary>关闭内存连接。</summary>
        /// <param name="statusCode">关闭状态。</param>
        /// <param name="reason">关闭原因。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>已完成任务。</returns>
        public ValueTask CloseAsync(int statusCode, string reason, CancellationToken cancellationToken = default) { State = WebSocketState.Closed; return ValueTask.CompletedTask; }

        /// <summary>释放无外部资源的连接。</summary>
        /// <returns>已完成任务。</returns>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
