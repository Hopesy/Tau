// 作者：xxx
using System.Net;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Mistral;
using Tau.Ai.Providers.PiMessages;
using Tau.Ai.Streaming;

namespace Tau.Ai.Tests;

/// <summary>【AI】【原生会话回归】检查 Mistral 与 PiMessages 的真实 HTTP 请求与跨轮重放。</summary>
public sealed class MistralAndPiMessagesTranscriptTests
{
    /// <summary>【PiMessages】【思考等级回归】成功与错误响应的供应商等级都能在下一轮网关报文中重放。</summary>
    /// <param name="error">是否返回错误事件。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PiMessages_ProviderThinkingLevelSurvivesNextRequest(bool error)
    {
        using var handler = new CaptureHandler("pi")
        {
            Response = error
                ? "data: {\"type\":\"error\",\"reason\":\"error\",\"errorMessage\":\"test failure\",\"providerThinkingLevel\":\"xhigh\"}\n\n"
                : "data: {\"type\":\"done\",\"reason\":\"stop\",\"providerThinkingLevel\":\"xhigh\"}\n\n"
        };
        using var client = new HttpClient(handler);
        var provider = new PiMessagesProvider(client);
        var model = Model(provider);
        var options = new StreamOptions { ApiKey = "test" };
        var response = await provider.Stream(model, new(null, [new UserMessage("hi")], null), options).ResultAsync;
        Assert.Equal("xhigh", response.ProviderThinkingLevel);
        Assert.Equal(error ? StopReason.Error : StopReason.EndTurn, response.StopReason);
        await provider.Stream(model, new(null, [new UserMessage("hi"), response, new UserMessage("next")], null), options).ResultAsync;
        using var document = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("xhigh", document.RootElement.GetProperty("context").GetProperty("messages")[1].GetProperty("providerThinkingLevel").GetString());
    }

    /// <summary>Mistral 只在模型显式启用时保留中途系统更新。</summary>
    /// <param name="enabled">系统消息能力。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mistral_RespectsSystemMessageCapability(bool? enabled)
    {
        var body = await RequestAsync("mistral", Context(), new() { SupportsMidConvoSystemMessages = enabled });
        var messages = body.GetProperty("messages").EnumerateArray().ToArray();
        if (enabled == true)
        {
            Assert.Equal(["system", "user", "system", "user"], messages.Select(message => message.GetProperty("role").GetString()));
            Assert.Equal("base\n\nold\n\ndiscard", messages[0].GetProperty("content").GetString());
            Assert.Equal("later\n\nUpdated system prompt section \"mode\":\n\nnew\n\nRemoved system prompt section \"drop\".", messages[2].GetProperty("content").GetString());
        }
        else
        {
            Assert.Equal(["system", "user", "user"], messages.Select(message => message.GetProperty("role").GetString()));
            Assert.Equal("base\n\nlater\n\nnew", messages[0].GetProperty("content").GetString());
        }
        Assert.Equal(["first", "second"], MistralTools(body));
    }

    /// <summary>Mistral 不使用新增工具消息，即使兼容标记全开也发送最终工具定义。</summary>
    /// <param name="change">工具变化种类。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("redefine")]
    [InlineData("repeat")]
    public async Task Mistral_AlwaysSendsCurrentTools(string change)
    {
        var first = Definition("first");
        var update = new SystemMessage("")
        {
            ToolsAdded = change switch { "add" => [Definition("second")], "redefine" => [first with { Description = "revised" }], "repeat" => [first], _ => null },
            ToolsRemoved = change == "remove" ? [new("first")] : null
        };
        var body = await RequestAsync("mistral", new(null,
            [new SystemMessage("base") { ToolsAdded = [first] }, new UserMessage("before"), update, new UserMessage("after")], null),
            new() { SupportsMidConvoSystemMessages = true, SupportsMidConvoToolAdditions = true, SupportsMidConvoToolChanges = true });
        var expected = change switch { "add" => new[] { "first", "second" }, "remove" => [], _ => ["first"] };
        Assert.Equal(expected, MistralTools(body));
        Assert.Equal(3, body.GetProperty("messages").GetArrayLength());
        Assert.DoesNotContain("tool_addition", body.GetRawText(), StringComparison.Ordinal);
        if (change == "redefine") Assert.Equal("revised", body.GetProperty("tools")[0].GetProperty("function").GetProperty("description").GetString());
    }

    /// <summary>跳过无正文的工具基线后，后续分节仍按更新语义渲染并清理非法代理字符。</summary>
    /// <param name="withHead">是否存在只有工具的开场。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mistral_LaterSystemUsesOriginalTranscriptPosition(bool withHead)
    {
        var messages = new List<ChatMessage>();
        if (withHead) messages.Add(new SystemMessage("") { ToolsAdded = [Definition("first")] });
        messages.Add(new UserMessage("hello"));
        messages.Add(new SystemMessage("up\ud800date") { Sections = new Dictionary<string, string?> { ["mode"] = "new\udfff" } });
        var body = await RequestAsync("mistral", new(null, messages, null), new() { SupportsMidConvoSystemMessages = true });
        Assert.Equal(2, body.GetProperty("messages").GetArrayLength());
        Assert.Equal("update\n\nUpdated system prompt section \"mode\":\n\nnew", body.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    /// <summary>不同公开入口统一处理旧字段，不重复开场声明，也不提前折叠原生会话。</summary>
    /// <param name="api">协议。</param>
    /// <param name="entry">调用入口。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("mistral", "direct")]
    [InlineData("mistral", "direct-simple")]
    [InlineData("mistral", "stream")]
    [InlineData("mistral", "simple")]
    [InlineData("mistral", "models")]
    [InlineData("mistral", "models-simple")]
    [InlineData("pi", "direct")]
    [InlineData("pi", "direct-simple")]
    [InlineData("pi", "stream")]
    [InlineData("pi", "simple")]
    [InlineData("pi", "models")]
    [InlineData("pi", "models-simple")]
    [InlineData("radius", "stream")]
    [InlineData("radius", "models-simple")]
    public async Task EntryPoints_NormalizeLegacyContextOnce(string api, string entry)
    {
        ChatMessage[] original = [new UserMessage("before"), new SystemMessage("later") { ToolsRemoved = [new("old")], ToolsAdded = [Definition("new")] }, new UserMessage("after")];
        var context = new LlmContext("legacy", original, [Definition("old")]);
        var body = await RequestAsync(api, context, new() { SupportsMidConvoSystemMessages = true }, entry);
        var wireContext = api != "mistral" ? body.GetProperty("context") : body;
        var messages = wireContext.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("legacy", messages[0].GetProperty("content").GetString());
        Assert.Equal("later", messages[2].GetProperty("content").GetString());
        if (api != "mistral")
        {
            Assert.False(wireContext.TryGetProperty("systemPrompt", out _));
            Assert.False(wireContext.TryGetProperty("tools", out _));
            Assert.Equal(0, messages[0].GetProperty("timestamp").GetInt64());
            Assert.Equal("old", messages[0].GetProperty("toolsAdded")[0].GetProperty("name").GetString());
            Assert.Equal("new", messages[2].GetProperty("toolsAdded")[0].GetProperty("name").GetString());
            Assert.Equal("old", messages[2].GetProperty("toolsRemoved")[0].GetProperty("name").GetString());
        }
        else Assert.Equal(["new"], MistralTools(body));
        Assert.Equal(3, original.Length);
        Assert.Equal("legacy", context.SystemPrompt);
        Assert.Equal("old", Assert.Single(context.Tools!).Name);
    }

    /// <summary>网关不依赖下游模型标记，完整保留系统段落删除、工具约束和时间戳。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task PiMessages_PreservesUncollapsedSystemDeclarations()
    {
        var context = Context();
        var first = (SystemMessage)context.Messages[0];
        var tools = new[] { Definition("first") with { ConstrainedSampling = new() { Type = "grammar", Variants = new Dictionary<string, string> { ["openai_lark"] = "start: WORD" } } } };
        var messages = context.Messages.ToArray();
        messages[0] = first with { ToolsAdded = tools, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(123) };
        var body = await RequestAsync("pi", context with { Messages = messages }, new() { SupportsMidConvoSystemMessages = false });
        var wire = body.GetProperty("context").GetProperty("messages");
        Assert.Equal(4, wire.GetArrayLength());
        Assert.Equal("old", wire[0].GetProperty("sections").GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, wire[2].GetProperty("sections").GetProperty("drop").ValueKind);
        Assert.Equal(123, wire[0].GetProperty("timestamp").GetInt64());
        var declaration = wire[0].GetProperty("toolsAdded")[0];
        Assert.Equal("object", declaration.GetProperty("parameters").GetProperty("type").GetString());
        Assert.False(declaration.TryGetProperty("parameterSchema", out _));
        Assert.Equal("start: WORD", declaration.GetProperty("constrainedSampling").GetProperty("variants").GetProperty("openai_lark").GetString());
        Assert.Equal("first", tools[0].Name);
    }

    /// <summary>各类消息必须发送完整正文、数值时间、对象参数与上游用量字段。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task PiMessages_UsesNativeMessageAndContentFields()
    {
        using var details = JsonDocument.Parse("{\"files\":[\"a.cs\"],\"empty\":null}");
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(234);
        var usage = new Usage(10, 5, 2, 3, Cost: new(1, 2, 3, 4)) { ReasoningTokens = 1, CacheWrite1hTokens = 2, TotalTokens = 20 };
        var response = new AssistantMessage([
            new TextContent("answer") { TextSignature = "text-signature" },
            new ThinkingContent("reason") { ThinkingSignature = "thinking-signature", Redacted = true },
            new ToolCallContent("original_call", "read", "{\"path\":\"a.cs\",\"nested\":{\"yes\":true}}") { ThoughtSignature = "tool-signature" }])
        {
            Api = "source-api", Provider = "source-provider", Model = "source-model", ResponseId = "response", ResponseModel = "response-model",
            Usage = usage, StopReason = StopReason.ToolUse, Timestamp = timestamp, EndTurn = false, RawStopReason = "native",
            Deferred = new("source-provider", "source-model", "source-api", "handle") { ExpiresAt = timestamp, PollAfterMs = 7, Data = details.RootElement.Clone() },
            Diagnostics = [new() { Type = "test", Timestamp = timestamp, Details = new Dictionary<string, object?> { ["count"] = 1 } }]
        };
        var body = await RequestAsync("pi", new(null, [
            new UserMessage([new TextContent("hello"), new ImageContent("abc", "image/png")]) { Timestamp = timestamp },
            response,
            new ToolResultMessage("original_call", [new TextContent("result"), new ImageContent("def", "image/jpeg")], true)
            { Details = details.RootElement.Clone(), Usage = usage, Timestamp = timestamp }], null));
        var messages = body.GetProperty("context").GetProperty("messages");
        Assert.Equal("hello", messages[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("abc", messages[0].GetProperty("content")[1].GetProperty("data").GetString());
        var assistant = messages[1];
        Assert.Equal("source-provider", assistant.GetProperty("provider").GetString());
        Assert.Equal("source-model", assistant.GetProperty("model").GetString());
        Assert.Equal("toolUse", assistant.GetProperty("stopReason").GetString());
        Assert.False(assistant.GetProperty("endTurn").GetBoolean());
        Assert.Equal(234, assistant.GetProperty("timestamp").GetInt64());
        Assert.Equal(234, assistant.GetProperty("deferred").GetProperty("expiresAt").GetInt64());
        Assert.Equal(234, assistant.GetProperty("diagnostics")[0].GetProperty("timestamp").GetInt64());
        var content = assistant.GetProperty("content");
        Assert.Equal("text-signature", content[0].GetProperty("textSignature").GetString());
        Assert.Equal("thinking-signature", content[1].GetProperty("thinkingSignature").GetString());
        Assert.True(content[1].GetProperty("redacted").GetBoolean());
        Assert.Equal("tool-signature", content[2].GetProperty("thoughtSignature").GetString());
        Assert.True(content[2].GetProperty("arguments").GetProperty("nested").GetProperty("yes").GetBoolean());
        Assert.Equal(10, assistant.GetProperty("usage").GetProperty("input").GetInt32());
        Assert.Equal(2, assistant.GetProperty("usage").GetProperty("cacheWrite1h").GetInt32());
        Assert.Equal(10, assistant.GetProperty("usage").GetProperty("cost").GetProperty("total").GetDecimal());
        Assert.False(assistant.GetProperty("usage").TryGetProperty("inputTokens", out _));
        Assert.Equal("read", messages[2].GetProperty("toolName").GetString());
        Assert.Equal("original_call", messages[2].GetProperty("toolCallId").GetString());
        Assert.True(messages[2].GetProperty("isError").GetBoolean());
        Assert.Equal(JsonValueKind.Null, messages[2].GetProperty("details").GetProperty("empty").ValueKind);
        Assert.Equal("image/jpeg", messages[2].GetProperty("content")[1].GetProperty("mimeType").GetString());
    }

    /// <summary>停止原因映射为网关字符串；旧记录缺失来源、用量和时间时补齐稳定默认值。</summary>
    /// <param name="reason">Tau 终态。</param>
    /// <param name="expected">网关终态。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(null, "pending")]
    [InlineData(StopReason.EndTurn, "stop")]
    [InlineData(StopReason.MaxTokens, "length")]
    [InlineData(StopReason.ToolUse, "toolUse")]
    [InlineData(StopReason.Error, "error")]
    [InlineData(StopReason.Aborted, "aborted")]
    [InlineData(StopReason.Deferred, "deferred")]
    [InlineData(StopReason.ContentFilter, "error")]
    public async Task PiMessages_MapsStopReasonsAndLegacyDefaults(StopReason? reason, string expected)
    {
        var body = await RequestAsync("pi", new(null, [new AssistantMessage([new TextContent("old")]) { StopReason = reason }], null));
        var message = body.GetProperty("context").GetProperty("messages")[0];
        Assert.Equal(expected, message.GetProperty("stopReason").GetString());
        Assert.Equal("pi-messages", message.GetProperty("api").GetString());
        Assert.Equal("transcript-test", message.GetProperty("provider").GetString());
        Assert.Equal(0, message.GetProperty("timestamp").GetInt64());
        Assert.Equal(0, message.GetProperty("usage").GetProperty("totalTokens").GetInt32());
    }

    /// <summary>损坏或非对象工具参数在发送 HTTP 之前返回可诊断错误。</summary>
    /// <param name="arguments">不合法参数。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task PiMessages_InvalidArgumentsDoNotSendRequest(string arguments)
    {
        using var handler = new CaptureHandler("pi");
        using var client = new HttpClient(handler);
        var provider = new PiMessagesProvider(client);
        var result = await provider.Stream(Model(provider), new(null, [new AssistantMessage([new ToolCallContent("call", "tool", arguments)])], null), new() { ApiKey = "test" }).ResultAsync;
        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Contains("JSON", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Bodies);
    }

    /// <summary>普通简化选项必须传递推理和工具选择，显式禁用缓存覆盖环境中的 long。</summary>
    /// <param name="level">推理等级。</param>
    /// <param name="wireLevel">网关等级；为空时应省略。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(ThinkingLevel.High, "high")]
    [InlineData(ThinkingLevel.ExtraHigh, "xhigh")]
    [InlineData(ThinkingLevel.Off, null)]
    public async Task PiMessages_SimpleOptionsUseWireNamesAndExplicitCacheNone(ThinkingLevel level, string? wireLevel)
    {
        using var handler = new CaptureHandler("pi");
        using var client = new HttpClient(handler);
        var provider = new PiMessagesProvider(client);
        using var toolChoice = JsonDocument.Parse("{\"type\":\"function\",\"function\":{\"name\":\"read\"}}");
        var result = await provider.StreamSimple(Model(provider), Context(), new SimpleStreamOptions
        {
            ApiKey = "test", Reasoning = level, ToolChoice = toolChoice.RootElement.Clone(),
            CacheRetention = CacheRetention.None, Env = new Dictionary<string, string> { ["PI_CACHE_RETENTION"] = "long" }
        }).ResultAsync;
        Assert.NotEqual(StopReason.Error, result.StopReason);
        using var document = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var options = document.RootElement.GetProperty("options");
        if (wireLevel is null) Assert.False(options.TryGetProperty("reasoning", out _));
        else Assert.Equal(wireLevel, options.GetProperty("reasoning").GetString());
        Assert.Equal("read", options.GetProperty("toolChoice").GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("none", options.GetProperty("cacheRetention").GetString());
        Assert.False(options.TryGetProperty("maxTokens", out _));
    }

    /// <summary>载荷回调看到规范化后的完整 wire 会话，并能替换最终请求。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task PiMessages_PayloadCallbackSeesCanonicalTranscriptAndCanReplaceBody()
    {
        using var handler = new CaptureHandler("pi");
        using var client = new HttpClient(handler);
        var provider = new PiMessagesProvider(client);
        var callbackCount = 0;
        var result = await provider.Stream(Model(provider), new("legacy", [new UserMessage("hello")], [Definition("read")]), new PiMessagesOptions
        {
            ApiKey = "test", Debug = true,
            OnPayload = (payload, _) =>
            {
                callbackCount++;
                var body = Assert.IsType<Dictionary<string, object>>(payload);
                var canonical = Assert.IsType<Dictionary<string, object>>(body["context"]);
                var messages = Assert.IsType<List<object>>(canonical["messages"]);
                var system = Assert.IsType<JsonElement>(messages[0]);
                Assert.Equal("legacy", system.GetProperty("content").GetString());
                Assert.Equal("read", system.GetProperty("toolsAdded")[0].GetProperty("name").GetString());
                var user = Assert.IsType<Dictionary<string, object>>(messages[1]);
                Assert.Equal("hello", Assert.IsType<List<Dictionary<string, object>>>(user["content"])[0]["text"]);
                Assert.False(canonical.ContainsKey("systemPrompt"));
                return ValueTask.FromResult<object?>(new Dictionary<string, object>(body) { ["marker"] = "replacement" });
            }
        }).ResultAsync;
        Assert.NotEqual(StopReason.Error, result.StopReason);
        Assert.Equal(1, callbackCount);
        Assert.Equal("?debug=1", handler.RequestUri!.Query);
        using var document = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("replacement", document.RootElement.GetProperty("marker").GetString());
    }

    /// <summary>响应中的签名和对象参数应在下一请求中原样重放，系统更新仍保持独立消息。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task PiMessages_ResponseSignaturesSurviveNextRequest()
    {
        using var handler = new CaptureHandler("pi")
        {
            Response = """
                data: {"type":"text_start","contentIndex":0}

                data: {"type":"text_end","contentIndex":0,"content":"answer","contentSignature":"text-signature"}

                data: {"type":"thinking_start","contentIndex":1}

                data: {"type":"thinking_end","contentIndex":1,"content":"","contentSignature":"opaque-thinking","redacted":true}

                data: {"type":"toolcall_start","contentIndex":2,"id":"original_call","toolName":"read"}

                data: {"type":"toolcall_end","contentIndex":2,"toolCall":{"type":"toolCall","id":"original_call","name":"read","arguments":{"path":"a.cs"},"thoughtSignature":"opaque-tool","namespace":"files"}}

                data: {"type":"done","reason":"toolUse","responseId":"response","usage":{"input":2,"output":3,"cacheWrite":1,"cacheWrite1h":1,"reasoning":2,"totalTokens":6}}

                """
        };
        using var client = new HttpClient(handler);
        var provider = new PiMessagesProvider(client);
        var model = Model(provider);
        var initial = new SystemMessage("base") { ToolsAdded = [Definition("read")] };
        var user = new UserMessage("hello");
        var options = new StreamOptions { ApiKey = "test" };
        var first = await provider.Stream(model, new(null, [initial, user], null), options).ResultAsync;
        Assert.Equal("text-signature", Assert.IsType<TextContent>(first.Content[0]).TextSignature);
        Assert.Equal("opaque-thinking", Assert.IsType<ThinkingContent>(first.Content[1]).ThinkingSignature);
        Assert.True(Assert.IsType<ThinkingContent>(first.Content[1]).Redacted);
        Assert.Equal("opaque-tool", Assert.IsType<ToolCallContent>(first.Content[2]).ThoughtSignature);
        Assert.Equal("files", Assert.IsType<ToolCallContent>(first.Content[2]).Namespace);
        Assert.Equal(1, first.Usage!.Value.CacheWrite1hTokens);
        var second = await provider.Stream(model, new(null, [initial, user, first,
            new ToolResultMessage("original_call", [new TextContent("done")]),
            new SystemMessage("later") { ToolsRemoved = [new("read")] }, new UserMessage("next")], null), options).ResultAsync;
        Assert.NotEqual(StopReason.Error, second.StopReason);
        using var document = JsonDocument.Parse(handler.Bodies[1]);
        var messages = document.RootElement.GetProperty("context").GetProperty("messages");
        var content = messages[2].GetProperty("content");
        Assert.Equal("text-signature", content[0].GetProperty("textSignature").GetString());
        Assert.Equal("opaque-thinking", content[1].GetProperty("thinkingSignature").GetString());
        Assert.True(content[1].GetProperty("redacted").GetBoolean());
        Assert.Equal("opaque-tool", content[2].GetProperty("thoughtSignature").GetString());
        Assert.Equal("files", content[2].GetProperty("namespace").GetString());
        Assert.Equal("a.cs", content[2].GetProperty("arguments").GetProperty("path").GetString());
        Assert.Equal("read", messages[3].GetProperty("toolName").GetString());
        Assert.Equal("later", messages[4].GetProperty("content").GetString());
        Assert.Equal("read", messages[4].GetProperty("toolsRemoved")[0].GetProperty("name").GetString());
    }

    /// <summary>创建同时含分节替换与删除的会话。</summary>
    /// <returns>测试上下文。</returns>
    private static LlmContext Context() => new(null, [
        new SystemMessage("base") { ToolsAdded = [Definition("first")], Sections = new Dictionary<string, string?> { ["mode"] = "old", ["drop"] = "discard" } },
        new UserMessage("before"),
        new SystemMessage("later") { ToolsAdded = [Definition("second")], Sections = new Dictionary<string, string?> { ["mode"] = "new", ["drop"] = null } },
        new UserMessage("after")], null);

    /// <summary>创建独立的工具 schema。</summary>
    /// <param name="name">工具名。</param>
    /// <returns>工具声明。</returns>
    private static Tool Definition(string name)
    {
        using var json = JsonDocument.Parse("{\"type\":\"object\"}");
        return new(name, "description", json.RootElement.Clone());
    }

    /// <summary>创建本地 HTTP 模拟模型。</summary>
    /// <param name="provider">协议实现。</param>
    /// <param name="compat">可选模型能力。</param>
    /// <returns>测试模型。</returns>
    private static Model Model(IStreamProvider provider, ModelCompatibility? compat = null) => new()
    {
        Id = "test", Name = "Test", Provider = "transcript-test", Api = provider.Api,
        BaseUrl = "https://example.invalid/v1", Compat = compat
    };

    /// <summary>经指定入口收集真实序列化请求。</summary>
    /// <param name="api">协议。</param>
    /// <param name="context">输入上下文。</param>
    /// <param name="compat">模型能力。</param>
    /// <param name="entry">公开入口。</param>
    /// <returns>独立持有内存的报文 JSON。</returns>
    private static async Task<JsonElement> RequestAsync(string api, LlmContext context, ModelCompatibility? compat = null, string entry = "direct")
    {
        using var handler = new CaptureHandler(api);
        using var client = new HttpClient(handler);
        IStreamProvider provider = api switch
        {
            "mistral" => new MistralProvider(client), "radius" => new RadiusProvider(httpClient: client), _ => new PiMessagesProvider(client)
        };
        var model = Model(provider, compat);
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var models = new Models([new ProviderDefinition(model.Provider, provider, [model])]);
        var options = new SimpleStreamOptions { ApiKey = "test" };
        var stream = entry switch
        {
            "direct-simple" => provider.StreamSimple(model, context, options),
            "stream" => StreamFunctions.Stream(registry, model, context, options),
            "simple" => StreamFunctions.StreamSimple(registry, model, context, options),
            "models" => models.Stream(model, context, options),
            "models-simple" => models.StreamSimple(model, context, options),
            _ => provider.Stream(model, context, options)
        };
        var result = await stream.ResultAsync;
        Assert.True(result.StopReason != StopReason.Error, result.ErrorMessage);
        using var json = JsonDocument.Parse(Assert.Single(handler.Bodies));
        return json.RootElement.Clone();
    }

    /// <summary>读取 Mistral 顶层活动工具，空集合允许省略字段。</summary>
    /// <param name="body">请求报文。</param>
    /// <returns>工具名顺序。</returns>
    private static string[] MistralTools(JsonElement body) => body.TryGetProperty("tools", out var tools)
        ? tools.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!).ToArray() : [];

    private sealed class CaptureHandler(string api) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public string? Response { get; init; }
        public Uri? RequestUri { get; private set; }
        /// <summary>记录请求报文并返回本地 SSE，无外部网络请求。</summary>
        /// <param name="request">HTTP 请求。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>模拟响应。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            RequestUri = request.RequestUri;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(Response ?? (api == "mistral"
                    ? "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"
                    : "data: {\"type\":\"done\",\"reason\":\"stop\",\"usage\":{\"input\":1,\"output\":1}}\n\n"), Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
