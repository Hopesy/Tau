// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Registry;
using Tau.Ai.Serialization;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Anthropic 会话回归】验证原生系统指令、内联工具变化、缓存和请求入口。</summary>
public sealed class AnthropicTranscriptTests
{
    /// <summary>原生增删固定顶层定义，缓存只标记最后一个开场工具和最终内容块。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Stream_UsesInlineDefinitionsAndStableInitialTools()
    {
        var context = Context();
        var initial = await CaptureAsync(context with { Messages = context.Messages.Take(2).ToArray() });
        var updated = await CaptureAsync(context);
        Assert.Equal(initial.Body.GetProperty("tools").GetRawText(), updated.Body.GetProperty("tools").GetRawText());
        var tools = updated.Body.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(new[] { "first", "__pi_deferred_placeholder__" }, tools.Select(tool => tool.GetProperty("name").GetString()));
        Assert.False(tools[0].TryGetProperty("defer_loading", out _));
        Assert.Equal("ephemeral", tools[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.True(tools[1].GetProperty("defer_loading").GetBoolean());
        Assert.False(tools[1].TryGetProperty("cache_control", out _));
        Assert.False(tools[1].TryGetProperty("eager_input_streaming", out _));
        Assert.Empty(tools[1].GetProperty("input_schema").GetProperty("properties").EnumerateObject());
        Assert.Empty(tools[1].GetProperty("input_schema").GetProperty("required").EnumerateArray());
        Assert.Contains("inline-tools-2026-09-15", initial.Betas);
        Assert.Contains("inline-tools-2026-09-15", updated.Betas);
        Assert.DoesNotContain("mid-conversation-tool-changes-2026-07-01", updated.Betas);
        Assert.Equal("base\n\nold section\n\nremove me", SystemText(updated.Body));

        var messages = Messages(updated.Body);
        Assert.Equal(new[] { "user", "user", "system" }, messages.Select(message => message.GetProperty("role").GetString()));
        var blocks = messages[^1].GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(new[] { "text", "tool_removal", "tool_addition" }, blocks.Select(block => block.GetProperty("type").GetString()));
        Assert.Equal("later\n\nUpdated system prompt section \"mode\":\n\nnew section\n\nRemoved system prompt section \"remove\".", blocks[0].GetProperty("text").GetString());
        Assert.Equal("tool_reference", blocks[1].GetProperty("tool").GetProperty("type").GetString());
        Assert.Equal("first", blocks[1].GetProperty("tool").GetProperty("name").GetString());
        var definition = blocks[2].GetProperty("tool").GetProperty("definition");
        Assert.Equal("tool_definition", blocks[2].GetProperty("tool").GetProperty("type").GetString());
        Assert.Equal("second", definition.GetProperty("name").GetString());
        Assert.Equal("second description", definition.GetProperty("description").GetString());
        Assert.True(definition.GetProperty("eager_input_streaming").GetBoolean());
        Assert.False(definition.TryGetProperty("cache_control", out _));
        Assert.False(definition.TryGetProperty("defer_loading", out _));
        Assert.True(blocks[2].TryGetProperty("cache_control", out _));
        Assert.False(blocks[0].TryGetProperty("cache_control", out _));
        Assert.False(messages[1].GetProperty("content")[0].TryGetProperty("cache_control", out _));
        Assert.Equal("old section", ((SystemMessage)context.Messages[0]).Sections!["mode"]);
        Assert.Equal("second", Assert.Single(Transcript.GetCurrentTools(context.Messages)).Name);
    }

    /// <summary>同名新增替换旧定义，大小写不同的工具仍需单独移除。</summary>
    /// <param name="name">新工具名称。</param>
    /// <param name="removalCount">期望移除块数量。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("first", 0)]
    [InlineData("First", 1)]
    public async Task Stream_RedefinitionsPreserveOriginalRequestDefinition(string name, int removalCount)
    {
        var replacement = Tool(name) with { Description = "changed", ParameterSchema = Json("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}") };
        var context = Context() with { Messages = [new SystemMessage("base") { ToolsAdded = [Tool("first")] }, new UserMessage("before"),
            new SystemMessage("") { ToolsRemoved = [new("first")], ToolsAdded = [replacement] }] };
        var capture = await CaptureAsync(context);
        Assert.Equal("first description", capture.Body.GetProperty("tools")[0].GetProperty("description").GetString());
        var blocks = Messages(capture.Body)[^1].GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(removalCount, blocks.Count(block => block.GetProperty("type").GetString() == "tool_removal"));
        var addition = Assert.Single(blocks, block => block.GetProperty("type").GetString() == "tool_addition");
        var definition = addition.GetProperty("tool").GetProperty("definition");
        Assert.Equal("changed", definition.GetProperty("description").GetString());
        Assert.Equal("string", definition.GetProperty("input_schema").GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
    }

    /// <summary>系统更新经过用户输入或工具结果时暂存，在下一条助手消息前按顺序刷新。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Stream_DoesNotInterruptToolUseAndResults()
    {
        var context = new LlmContext(null,
        [
            new SystemMessage("base") { ToolsAdded = [Tool("first")] }, new UserMessage("start"),
            new AssistantMessage([new ToolCallContent("one", "first", "{}"), new ToolCallContent("two", "first", "{}")]),
            new SystemMessage("between call and results"),
            new ToolResultMessage("one", [new TextContent("one result")]), new ToolResultMessage("two", [new TextContent("two result")]),
            new SystemMessage("before user") { ToolsAdded = [Tool("second")] }, new UserMessage("more"),
            new AssistantMessage([new TextContent("finished")]), new SystemMessage("last update")
        ], null);
        var capture = await CaptureAsync(context);
        var messages = Messages(capture.Body);
        Assert.Equal(new[] { "user", "assistant", "user", "user", "system", "system", "assistant", "system" }, messages.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal(new[] { "one", "two" }, messages[2].GetProperty("content").EnumerateArray().Select(block => block.GetProperty("tool_use_id").GetString()));
        Assert.Equal("between call and results", messages[4].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("before user", messages[5].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("last update", messages[7].GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>缺少任一能力时禁用工具变化；指令合并只由系统消息能力决定。</summary>
    /// <param name="system">是否支持中途系统消息。</param>
    /// <param name="tools">是否支持原生工具变化。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(null, null)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, null)]
    public async Task Stream_RequiresBothNativeCapabilities(bool? system, bool? tools)
    {
        var capture = await CaptureAsync(Context(), Native() with { SupportsMidConvoSystemMessages = system, SupportsMidConvoToolChanges = tools });
        Assert.DoesNotContain("inline-tools-2026-09-15", capture.Betas);
        Assert.Equal("second", Assert.Single(capture.Body.GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
        if (system == true)
        {
            Assert.Equal("base\n\nold section\n\nremove me", SystemText(capture.Body));
            var update = Messages(capture.Body)[^1];
            Assert.Equal("system", update.GetProperty("role").GetString());
            Assert.Equal("text", Assert.Single(update.GetProperty("content").EnumerateArray()).GetProperty("type").GetString());
        }
        else
        {
            Assert.Equal("base\n\nlater\n\nnew section", SystemText(capture.Body));
            Assert.All(Messages(capture.Body), message => Assert.Equal("user", message.GetProperty("role").GetString()));
        }
    }

    /// <summary>没有开场活动工具时不声明孤立占位工具，改为发送当前工具集合。</summary>
    /// <param name="hasInitialSystem">是否有仅包含文本的开场声明。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stream_RequiresInitialActiveTool(bool hasInitialSystem)
    {
        ChatMessage[] messages = [new UserMessage("before"), new SystemMessage("later") { ToolsAdded = [Tool("late")] }];
        if (hasInitialSystem) messages = [new SystemMessage("base"), .. messages];
        var capture = await CaptureAsync(new(null, messages, null));
        Assert.DoesNotContain("inline-tools-2026-09-15", capture.Betas);
        Assert.Equal("late", Assert.Single(capture.Body.GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("later", Messages(capture.Body)[^1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>各类最终块正确获得缓存标记，助手结尾不回溯用户消息添加标记。</summary>
    /// <param name="ending">消息结尾类型。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("text")]
    [InlineData("addition")]
    [InlineData("removal")]
    [InlineData("user")]
    [InlineData("assistant")]
    [InlineData("tool_result")]
    public async Task Stream_PlacesCacheOnlyOnFinalEligibleBlock(string ending)
    {
        ChatMessage final = ending switch
        {
            "text" => new SystemMessage("updated"),
            "addition" => new SystemMessage("") { ToolsAdded = [Tool("second")] },
            "removal" => new SystemMessage("") { ToolsRemoved = [new("first")] },
            "assistant" => new AssistantMessage([new TextContent("done")]),
            "tool_result" => new ToolResultMessage("call", [new TextContent("result")]),
            _ => new UserMessage("last")
        };
        ChatMessage[] messages = [new SystemMessage("base") { ToolsAdded = [Tool("first")] }, new UserMessage("before")];
        if (ending == "tool_result") messages = [.. messages, new AssistantMessage([new ToolCallContent("call", "first", "{}")])];
        var capture = await CaptureAsync(new(null, [.. messages, final], null));
        var converted = Messages(capture.Body);
        var cached = converted.SelectMany(message => message.GetProperty("content").EnumerateArray()).Where(block => block.TryGetProperty("cache_control", out _)).ToArray();
        if (ending == "assistant") Assert.Empty(cached);
        else Assert.Equal(converted[^1].GetProperty("content").EnumerateArray().Last().GetRawText(), Assert.Single(cached).GetRawText());
        if (ending == "removal") Assert.Empty(Transcript.GetCurrentTools([.. messages, final]));
    }

    /// <summary>缓存和流式兼容设置同时作用于顶层与内联定义，关闭缓存后不泄漏标记。</summary>
    /// <param name="retention">缓存策略。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(CacheRetention.None)]
    [InlineData(CacheRetention.Long)]
    public async Task Stream_RespectsCacheAndStreamingCompatibility(CacheRetention retention)
    {
        var capture = await CaptureAsync(Context(), Native() with
        {
            SupportsLongCacheRetention = false, SupportsCacheControlOnTools = false, SupportsEagerToolInputStreaming = false
        }, new StreamOptions { CacheRetention = retention });
        Assert.Contains("fine-grained-tool-streaming-2025-05-14", capture.Betas);
        Assert.Contains("inline-tools-2026-09-15", capture.Betas);
        Assert.False(capture.Body.GetProperty("tools")[0].TryGetProperty("cache_control", out _));
        Assert.False(capture.Body.GetProperty("tools")[0].TryGetProperty("eager_input_streaming", out _));
        var last = Messages(capture.Body)[^1].GetProperty("content").EnumerateArray().Last();
        Assert.False(last.GetProperty("tool").GetProperty("definition").TryGetProperty("eager_input_streaming", out _));
        if (retention == CacheRetention.None) Assert.DoesNotContain("cache_control", capture.Body.GetRawText());
        else Assert.False(last.GetProperty("cache_control").TryGetProperty("ttl", out _));
    }

    /// <summary>模型和请求头能覆盖或清除自动 beta，不追加调用方没有选择的特性。</summary>
    /// <param name="overrideValue">请求头覆盖值。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("custom-beta")]
    [InlineData("")]
    public async Task Stream_RespectsExplicitBetaHeaders(string overrideValue)
    {
        var model = Model(Native()) with { Headers = new Dictionary<string, string> { ["anthropic-beta"] = "model-beta" } };
        var capture = await CaptureAsync(Context(), options: new StreamOptions { Headers = new Dictionary<string, string> { ["Anthropic-Beta"] = overrideValue } }, model: model);
        Assert.Equal(overrideValue.Length == 0 ? [] : new[] { overrideValue }, capture.Betas);
    }

    /// <summary>旧式上下文经直接、统一和组合路由入口后仍保留原生工具增删。</summary>
    /// <param name="entry">入口类型。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("direct")]
    [InlineData("direct-simple")]
    [InlineData("stream")]
    [InlineData("simple")]
    [InlineData("models")]
    [InlineData("models-simple")]
    [InlineData("routing")]
    [InlineData("alias")]
    public async Task PublicEntries_PreserveLegacyBaselineAndNativeChanges(string entry)
    {
        var context = new LlmContext("legacy", [new UserMessage("before"), new SystemMessage("later") { ToolsAdded = [Tool("second")] }], [Tool("first")]);
        var capture = await CaptureAsync(context, entry: entry);
        Assert.Equal("legacy", SystemText(capture.Body));
        Assert.Equal("first", capture.Body.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("second", Messages(capture.Body)[^1].GetProperty("content")[1].GetProperty("tool").GetProperty("definition").GetProperty("name").GetString());
        Assert.Contains("inline-tools-2026-09-15", capture.Betas);
    }

    /// <summary>工具变化能力支持配置继承、覆盖和源生成序列化往返。</summary>
    [Fact]
    public void Configuration_PreservesToolChangeCapability()
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-anthropic-transcript-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {"providers":{"test":{"compat":{"supportsMidConvoSystemMessages":true,"supportsMidConvoToolChanges":true},"models":[{"id":"enabled"},{"id":"disabled","compat":{"supportsMidConvoToolChanges":false}}]}}}
                """);
            var models = new ModelConfigurationStore([path]).ApplyToModels([], ModelTypes.Chat);
            var enabled = models.Single(model => model.Id == "enabled");
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(enabled, TauAiJsonContext.Default.Model), TauAiJsonContext.Default.Model)!;
            Assert.True(restored.Compat!.SupportsMidConvoToolChanges);
            Assert.True(restored.Compat.SupportsMidConvoSystemMessages);
            Assert.False(models.Single(model => model.Id == "disabled").Compat!.SupportsMidConvoToolChanges);
        }
        finally { File.Delete(path); }
    }

    /// <summary>创建包括段落补丁、工具移除和新增的完整上下文。</summary>
    /// <returns>包含原生系统声明的上下文。</returns>
    private static LlmContext Context() => new(null,
    [
        new SystemMessage("base") { ToolsAdded = [Tool("first")], Sections = new Dictionary<string, string?> { ["mode"] = "old section", ["remove"] = "remove me" } },
        new UserMessage("before"),
        new SystemMessage("later") { ToolsRemoved = [new("first")], ToolsAdded = [Tool("second")], Sections = new Dictionary<string, string?> { ["mode"] = "new section", ["remove"] = null } },
        new UserMessage("after")
    ], null);

    /// <summary>创建同时支持原生指令和工具变化的配置。</summary>
    /// <returns>显式启用的兼容能力。</returns>
    private static ModelCompatibility Native() => new() { SupportsMidConvoSystemMessages = true, SupportsMidConvoToolChanges = true };

    /// <summary>创建隔离的测试模型。</summary>
    /// <param name="compat">协议能力。</param>
    /// <returns>使用模拟地址的模型。</returns>
    private static Model Model(ModelCompatibility compat) => new() { Id = "test", Name = "Test", Api = "anthropic-messages", Provider = "transcript-test", BaseUrl = "https://example.invalid", Compat = compat };

    /// <summary>创建普通函数定义。</summary>
    /// <param name="name">工具名。</param>
    /// <returns>可独立持有的定义。</returns>
    private static Tool Tool(string name) => new(name, name + " description", Json("{\"type\":\"object\"}"));

    /// <summary>解析独立 JSON 节点。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>节点副本。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    /// <summary>提取协议消息数组。</summary>
    /// <param name="body">请求报文。</param>
    /// <returns>消息快照。</returns>
    private static JsonElement[] Messages(JsonElement body) => body.GetProperty("messages").EnumerateArray().ToArray();

    /// <summary>兼容有缓存和无缓存的顶层系统提示。</summary>
    /// <param name="body">请求报文。</param>
    /// <returns>系统提示文本。</returns>
    private static string? SystemText(JsonElement body) => body.GetProperty("system") is var system && system.ValueKind == JsonValueKind.String
        ? system.GetString() : system[0].GetProperty("text").GetString();

    /// <summary>捕获实际 HTTP 请求并返回成功的模拟 Anthropic 事件。</summary>
    /// <param name="context">请求上下文。</param>
    /// <param name="compat">模型能力覆盖。</param>
    /// <param name="options">请求选项覆盖。</param>
    /// <param name="entry">公开入口类型。</param>
    /// <param name="model">完整模型覆盖。</param>
    /// <returns>实际序列化的请求及 beta 列表。</returns>
    private static async Task<Capture> CaptureAsync(LlmContext context, ModelCompatibility? compat = null, StreamOptions? options = null, string entry = "direct", Model? model = null)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"test","usage":{"input_tokens":1,"output_tokens":0}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

            event: message_stop
            data: {"type":"message_stop"}

            """));
        using var client = new HttpClient(handler);
        IStreamProvider provider = new AnthropicProvider(client);
        model ??= Model(compat ?? Native());
        options = (options ?? new StreamOptions { CacheRetention = CacheRetention.Short }) with { ApiKey = "test-key" };
        var simple = new SimpleStreamOptions { ApiKey = "test-key", CacheRetention = options.CacheRetention };
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        if (entry is "routing" or "alias")
        {
            BuiltInProviders.RegisterAll(registry, new ModelConfigurationStore([]), client);
            model = model with { Provider = entry == "routing" ? "cloudflare-ai-gateway" : "minimax" };
            provider = registry.Get(model.Provider);
        }
        var models = new Models([new ProviderDefinition(model.Provider, provider, [model])]);
        var stream = entry switch
        {
            "direct-simple" => provider.StreamSimple(model, context, simple),
            "stream" => StreamFunctions.Stream(registry, model, context, options),
            "simple" => StreamFunctions.StreamSimple(registry, model, context, simple),
            "models" => models.Stream(model, context, options),
            "models-simple" or "routing" or "alias" => models.StreamSimple(model, context, simple),
            _ => provider.Stream(model, context, options)
        };
        var result = await stream.ResultAsync;
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        var request = Assert.Single(handler.Requests);
        var betas = request.Headers.TryGetValues("anthropic-beta", out var values)
            ? string.Join(",", values).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        return new(Json(handler.CapturedBody), betas);
    }

    private sealed record Capture(JsonElement Body, string[] Betas);
}
