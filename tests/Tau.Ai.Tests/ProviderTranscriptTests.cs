// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Registry;
using Tau.Ai.Serialization;

namespace Tau.Ai.Tests;

/// <summary>【AI】【原生会话回归】在实际 HTTP 报文层验证声明位置、工具新增和兼容回退。</summary>
public sealed class ProviderTranscriptTests
{
    /// <summary>组合 provider 按实际目标协议决定保留声明或合并回退。</summary>
    /// <param name="api">目标协议。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("openai-responses")]
    [InlineData("anthropic-messages")]
    public async Task RoutingProvider_UsesResolvedTransportCapability(string api)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent("captured request")
        });
        using var client = new HttpClient(handler);
        var registry = new ProviderRegistry();
        BuiltInProviders.RegisterAll(registry, new ModelConfigurationStore([]), client);
        var provider = registry.Get("cloudflare-ai-gateway");
        var model = new Model { Id = "test", Name = "Test", Provider = "cloudflare-ai-gateway", Api = api, BaseUrl = "https://example.invalid/v1",
            Compat = NativeCompatibility() with { SupportsMidConvoSystemMessages = api == "openai-responses" } };
        var models = new Models([new ProviderDefinition(model.Provider, provider, [model])]);
        var response = await models.StreamSimple(model, Context(), new() { ApiKey = "key", CacheRetention = CacheRetention.None }).ResultAsync;
        Assert.Equal(StopReason.Error, response.StopReason);
        Assert.Contains("captured request", response.ErrorMessage);
        using var document = JsonDocument.Parse(handler.CapturedBody);
        var body = document.RootElement;
        if (api == "openai-responses")
        {
            Assert.Equal("first", ToolNames(body, "responses").Single());
            var addition = Assert.Single(Messages(body, "responses"), item => item.TryGetProperty("type", out var type) && type.GetString() == "additional_tools");
            Assert.Equal("second", ToolNames(addition, "responses").Single());
        }
        else
        {
            Assert.Equal("base\n\nlater\n\nnew section", body.GetProperty("system")[0].GetProperty("text").GetString());
            Assert.Equal(new[] { "first", "second" }, ToolNames(body, "anthropic"));
            Assert.DoesNotContain(body.GetProperty("messages").EnumerateArray(), item => item.GetProperty("role").GetString() == "system");
        }
    }

    /// <summary>原生指令和工具新增分别受能力开关控制，并尊重关闭 developer 角色的配置。</summary>
    /// <param name="api">协议。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    [InlineData("responses")]
    [InlineData("codex")]
    [InlineData("azure")]
    public async Task Stream_SystemUpdatesDoNotRequireToolAdditionCapability(string api)
    {
        var body = await RequestAsync(api, Context(), NativeCompatibility() with
        {
            SupportsDeveloperRole = false, SupportsMidConvoToolAdditions = false,
            SupportsAdditionalTools = false, SupportsToolSearch = false
        });
        Assert.Equal(new[] { "first", "second" }, ToolNames(body, api));
        var messages = Messages(body, api);
        Assert.DoesNotContain(messages, item => item.TryGetProperty("tools", out _));
        Assert.DoesNotContain(messages, item => item.TryGetProperty("role", out var role) && role.GetString() == "developer");
        var update = messages[api == "codex" ? 1 : 2];
        Assert.Equal("system", update.GetProperty("role").GetString());
        Assert.StartsWith("later\n\nUpdated", update.GetProperty("content").GetString());
    }

    /// <summary>原生接口保留中途段落更新，并在原位置声明新工具。</summary>
    /// <param name="api">待验证的协议。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    [InlineData("responses")]
    [InlineData("codex")]
    [InlineData("azure")]
    public async Task Stream_PreservesSystemUpdatesAndToolAdditionPositions(string api)
    {
        var context = Context();
        var body = await RequestAsync(api, context, NativeCompatibility());
        Assert.Equal("first", ToolNames(body, api).Single());
        var messages = Messages(body, api);
        var offset = api == "codex" ? 0 : 1;
        if (api == "codex") Assert.Equal("base\n\nold section\n\nold removal", body.GetProperty("instructions").GetString());
        else Assert.Equal("base\n\nold section\n\nold removal", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[offset].GetProperty("role").GetString());
        var addition = messages[offset + 1];
        Assert.Equal("second", ToolNames(addition, api).Single());
        if (api is "chat" or "compatible")
        {
            Assert.Equal("system", addition.GetProperty("role").GetString());
            Assert.False(addition.TryGetProperty("content", out _));
        }
        else Assert.Equal("additional_tools", addition.GetProperty("type").GetString());
        var update = messages[offset + 2];
        Assert.Equal("developer", update.GetProperty("role").GetString());
        Assert.Equal("later\n\nUpdated system prompt section \"mode\":\n\nnew section\n\nRemoved system prompt section \"remove\".",
            update.GetProperty("content").GetString());
        Assert.Equal("user", messages[offset + 3].GetProperty("role").GetString());
        Assert.Equal(offset + 4, messages.Length);
        Assert.Equal("old section", ((SystemMessage)context.Messages[0]).Sections!["mode"]);
    }

    /// <summary>未开启中途指令时合并段落和工具，即使单独开启工具新增能力。</summary>
    /// <param name="api">待验证的协议。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    [InlineData("responses")]
    [InlineData("codex")]
    [InlineData("azure")]
    public async Task Stream_CollapsesUnsupportedMidConversationUpdates(string api)
    {
        var body = await RequestAsync(api, Context(), NativeCompatibility() with { SupportsMidConvoSystemMessages = null });
        Assert.Equal(new[] { "first", "second" }, ToolNames(body, api));
        var messages = Messages(body, api);
        var prompt = api == "codex" ? body.GetProperty("instructions").GetString() : messages[0].GetProperty("content").GetString();
        Assert.Equal("base\n\nlater\n\nnew section", prompt);
        Assert.Equal(api == "codex" ? 2 : 3, messages.Length);
        Assert.DoesNotContain(messages, item => item.TryGetProperty("tools", out _));
    }

    /// <summary>移除、替换和相同定义的再次声明均回退到完整当前工具集合。</summary>
    /// <param name="api">协议。</param>
    /// <param name="change">非纯新增变化类型。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("chat", "remove")]
    [InlineData("chat", "replace")]
    [InlineData("chat", "repeat")]
    [InlineData("compatible", "remove")]
    [InlineData("compatible", "replace")]
    [InlineData("compatible", "repeat")]
    [InlineData("responses", "remove")]
    [InlineData("responses", "replace")]
    [InlineData("responses", "repeat")]
    [InlineData("codex", "remove")]
    [InlineData("codex", "replace")]
    [InlineData("codex", "repeat")]
    [InlineData("azure", "remove")]
    [InlineData("azure", "replace")]
    [InlineData("azure", "repeat")]
    public async Task Stream_NonAdditiveChangesUseCurrentTools(string api, string change)
    {
        var original = Tool("first");
        var next = change == "replace" ? original with { Description = "changed" } : original;
        var context = new LlmContext(null,
        [
            new SystemMessage("base") { ToolsAdded = [original] }, new UserMessage("before"),
            new SystemMessage("update")
            {
                ToolsRemoved = change is "remove" or "replace" ? [new("first")] : null,
                ToolsAdded = change == "remove" ? [Tool("second")] : [next]
            }, new UserMessage("after")
        ], null);
        var body = await RequestAsync(api, context, NativeCompatibility());
        Assert.Equal(change == "remove" ? "second" : "first", ToolNames(body, api).Single());
        var declaration = body.GetProperty("tools")[0];
        if (api is "chat" or "compatible") declaration = declaration.GetProperty("function");
        Assert.Equal(change == "replace" ? "changed" : "description", declaration.GetProperty("description").GetString());
        var messages = Messages(body, api);
        Assert.DoesNotContain(messages, item => item.TryGetProperty("tools", out _));
        Assert.Contains(messages, item => item.TryGetProperty("content", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == "update");
    }

    /// <summary>客户端工具搜索使用稳定标识配对，并标记延迟加载定义。</summary>
    /// <param name="api">Responses 协议变体。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("responses")]
    [InlineData("codex")]
    [InlineData("azure")]
    public async Task Stream_EncodesToolSearchFallback(string api)
    {
        var body = await RequestAsync(api, Context(), NativeCompatibility() with { SupportsAdditionalTools = false });
        var messages = Messages(body, api);
        var call = Assert.Single(messages, item => item.TryGetProperty("type", out var type) && type.GetString() == "tool_search_call");
        var output = Assert.Single(messages, item => item.TryGetProperty("type", out var type) && type.GetString() == "tool_search_output");
        Assert.Equal("pi_tool_load_" + ShortHash.Compute("system:1:second"), call.GetProperty("call_id").GetString());
        Assert.Equal(call.GetProperty("call_id").GetString(), output.GetProperty("call_id").GetString());
        Assert.Equal("client", call.GetProperty("execution").GetString());
        Assert.Equal("completed", output.GetProperty("status").GetString());
        Assert.Equal("second", call.GetProperty("arguments").GetProperty("query").GetString());
        Assert.Equal(1, call.GetProperty("arguments").GetProperty("limit").GetInt32());
        Assert.True(output.GetProperty("tools")[0].GetProperty("defer_loading").GetBoolean());
        Assert.Equal("first", ToolNames(body, api).Single());
    }

    /// <summary>没有开场声明时，中途首次出现的工具应留在原位置，不能提升到顶层。</summary>
    /// <param name="api">协议。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    [InlineData("responses")]
    [InlineData("codex")]
    [InlineData("azure")]
    public async Task Stream_KeepsLateFirstDeclarationInPlace(string api)
    {
        var context = new LlmContext(null, [new UserMessage("before"), new SystemMessage("") { ToolsAdded = [Tool("late")] }], null);
        var body = await RequestAsync(api, context, NativeCompatibility());
        Assert.False(body.TryGetProperty("tools", out _));
        var messages = Messages(body, api);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("late", ToolNames(messages[1], api).Single());
        Assert.Equal(2, messages.Length);
        if (api == "codex") Assert.Equal("You are a helpful assistant.", body.GetProperty("instructions").GetString());
    }

    /// <summary>Models 和旧入口的普通及简化路径不能提前折叠原生会话。</summary>
    /// <param name="api">协议。</param>
    /// <param name="entry">请求入口。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("chat", "stream")]
    [InlineData("chat", "simple")]
    [InlineData("chat", "models")]
    [InlineData("chat", "models-simple")]
    [InlineData("responses", "stream")]
    [InlineData("responses", "simple")]
    [InlineData("responses", "models")]
    [InlineData("responses", "models-simple")]
    public async Task PublicEntries_PreserveLegacyBaselineAndNativeDeltas(string api, string entry)
    {
        var context = new LlmContext("legacy", [new UserMessage("before"), new SystemMessage("later") { ToolsAdded = [Tool("second")] }], [Tool("first")]);
        var body = await RequestAsync(api, context, NativeCompatibility(), entry);
        Assert.Equal("first", ToolNames(body, api).Single());
        var messages = Messages(body, api);
        Assert.Equal("legacy", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("second", ToolNames(messages[2], api).Single());
        Assert.Equal("later", messages[3].GetProperty("content").GetString());
    }

    /// <summary>兼容能力支持模型配置继承、显式关闭和源生成 JSON 往返。</summary>
    [Fact]
    public void Configuration_PreservesTranscriptCapabilityOverrides()
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-transcript-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {"providers":{"test":{"compat":{"supportsMidConvoSystemMessages":true,"supportsMidConvoToolAdditions":true,"supportsAdditionalTools":true,"supportsToolSearch":true},
                  "models":[{"id":"enabled"},{"id":"disabled","compat":{"supportsMidConvoSystemMessages":false,"supportsMidConvoToolAdditions":false,"supportsAdditionalTools":false,"supportsToolSearch":false}}]}}}
                """);
            var configured = new ModelConfigurationStore([path]).ApplyToModels([], ModelTypes.Chat);
            var enabled = configured.Single(model => model.Id == "enabled");
            var stored = JsonSerializer.Serialize(enabled, TauAiJsonContext.Default.Model);
            var restored = JsonSerializer.Deserialize(stored, TauAiJsonContext.Default.Model)!;
            Assert.True(restored.Compat!.SupportsMidConvoSystemMessages);
            Assert.True(restored.Compat.SupportsMidConvoToolAdditions);
            Assert.True(restored.Compat.SupportsAdditionalTools);
            Assert.True(restored.Compat.SupportsToolSearch);
            var disabled = configured.Single(model => model.Id == "disabled").Compat!;
            Assert.False(disabled.SupportsMidConvoSystemMessages);
            Assert.False(disabled.SupportsMidConvoToolAdditions);
            Assert.False(disabled.SupportsAdditionalTools);
            Assert.False(disabled.SupportsToolSearch);
        }
        finally { File.Delete(path); }
    }

    /// <summary>创建两轮用户输入和一次包含段落、工具的系统更新。</summary>
    /// <returns>仅使用系统声明的上下文。</returns>
    private static LlmContext Context() => new(null,
    [
        new SystemMessage("base") { ToolsAdded = [Tool("first")], Sections = new Dictionary<string, string?> { ["mode"] = "old section", ["remove"] = "old removal" } },
        new UserMessage("before"),
        new SystemMessage("later") { ToolsAdded = [Tool("second")], Sections = new Dictionary<string, string?> { ["mode"] = "new section", ["remove"] = null } },
        new UserMessage("after")
    ], null);

    /// <summary>建立支持所有本批原生路径的显式能力配置。</summary>
    /// <returns>模型兼容选项。</returns>
    private static ModelCompatibility NativeCompatibility() => new()
    {
        SupportsDeveloperRole = true, SupportsMidConvoSystemMessages = true, SupportsMidConvoToolAdditions = true,
        SupportsAdditionalTools = true, SupportsToolSearch = true
    };

    /// <summary>建立普通函数工具。</summary>
    /// <param name="name">工具名。</param>
    /// <returns>工具声明。</returns>
    private static Tool Tool(string name)
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        return new(name, "description", schema.RootElement.Clone());
    }

    /// <summary>通过独立 HTTP 模拟客户端获得真实序列化请求。</summary>
    /// <param name="api">协议别名。</param>
    /// <param name="context">请求上下文。</param>
    /// <param name="compat">模型能力。</param>
    /// <param name="entry">公开请求入口。</param>
    /// <returns>独立的报文 JSON 节点。</returns>
    private static async Task<JsonElement> RequestAsync(string api, LlmContext context, ModelCompatibility compat, string entry = "direct")
    {
        var chat = api is "chat" or "compatible";
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(chat
            ? "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"
            : "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\"}}\n\n"));
        using var client = new HttpClient(handler);
        IStreamProvider provider = api switch
        {
            "chat" => new OpenAiProvider(client),
            "compatible" => new OpenAiCompatibleProvider("test-compatible", "https://example.invalid/v1", httpClient: client),
            "responses" => new OpenAiResponsesProvider(client),
            "codex" => new OpenAiCodexResponsesProvider(client),
            "azure" => new AzureOpenAiResponsesProvider(client),
            _ => throw new ArgumentException("Unknown protocol", nameof(api))
        };
        using var lifetime = provider as IDisposable;
        var model = new Model { Id = "test", Name = "Test", Api = provider.Api, Provider = "transcript-test", BaseUrl = "https://example.invalid/v1", Reasoning = true, Compat = compat };
        var options = new SimpleStreamOptions { ApiKey = api == "codex" ? OpenAiResponsesSharedTests.BuildFakeJwt("transcript") : "key", Transport = StreamTransport.Sse };
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var models = new Models([new ProviderDefinition(model.Provider, provider, [model])]);
        StreamOptions directOptions = api == "azure" ? new AzureOpenAiResponsesOptions { ApiKey = "key" } : options;
        var stream = entry switch
        {
            "stream" => StreamFunctions.Stream(registry, model, context, options),
            "simple" => StreamFunctions.StreamSimple(registry, model, context, options),
            "models" => models.Stream(model, context, options),
            "models-simple" => models.StreamSimple(model, context, options),
            _ => provider.Stream(model, context, directOptions)
        };
        var result = await stream.ResultAsync;
        Assert.True(result.StopReason != StopReason.Error, result.ErrorMessage);
        using var document = JsonDocument.Parse(handler.CapturedBody);
        return document.RootElement.Clone();
    }

    /// <summary>读取不同协议的消息数组。</summary>
    /// <param name="body">请求报文。</param>
    /// <param name="api">协议别名。</param>
    /// <returns>协议消息快照。</returns>
    private static JsonElement[] Messages(JsonElement body, string api) => body.GetProperty(api is "chat" or "compatible" ? "messages" : "input").EnumerateArray().ToArray();

    /// <summary>读取顶层或消息内的工具名。</summary>
    /// <param name="container">包含 tools 的对象。</param>
    /// <param name="api">协议别名。</param>
    /// <returns>保持传输顺序的名称列表。</returns>
    private static string[] ToolNames(JsonElement container, string api) => container.GetProperty("tools").EnumerateArray()
        .Select(tool => (api is "chat" or "compatible" ? tool.GetProperty("function") : tool).GetProperty("name").GetString()!).ToArray();
}
