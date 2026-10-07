// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Anthropic 预算回归】通过实际 HTTP 报文验证公开入口的一致性。</summary>
public sealed class AnthropicTokenBudgetTests
{
    private const string Success = """
        event: message_start
        data: {"type":"message_start","message":{"id":"r","usage":{"input_tokens":1,"output_tokens":0}}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    /// <summary>固定思考预算追加到显式回答额度，缺省时使用模型总上限，ExtraHigh 使用 High 预算。</summary>
    /// <param name="entry">公开调用入口。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("direct")]
    [InlineData("simple")]
    [InlineData("configured")]
    [InlineData("models")]
    public async Task FixedThinking_AddsAndClampsBudget(string entry)
    {
        var model = Model() with { MaxOutputTokens = 20_000, ContextWindow = 100_000 };
        var body = await SendAsync(entry, model, Context(), new() { Reasoning = ThinkingLevel.High, MaxTokens = 2_048 });
        AssertBudget(body, 18_432, 16_384);
        body = await SendAsync(entry, model, Context(), new() { Reasoning = ThinkingLevel.ExtraHigh });
        AssertBudget(body, 20_000, 16_384);
        body = await SendAsync(entry, model with { MaxOutputTokens = 4_096 }, Context(), new() { Reasoning = ThinkingLevel.High });
        AssertBudget(body, 4_096, 3_072);
        body = await SendAsync(entry, model, Context(), new() { Reasoning = ThinkingLevel.High, MaxTokens = 2_048, ThinkingBudgets = new() { High = 3_333 } });
        AssertBudget(body, 5_381, 3_333);
    }

    /// <summary>追加思考后必须再次限制上下文；极小余量保留上游零预算回退行为。</summary>
    /// <param name="entry">公开调用入口。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("direct")]
    [InlineData("simple")]
    [InlineData("configured")]
    [InlineData("models")]
    public async Task ContextRoom_ClampsAfterThinking(string entry)
    {
        var options = new SimpleStreamOptions { Reasoning = ThinkingLevel.High, MaxTokens = 2_048 };
        var body = await SendAsync(entry, Model(), Context(), options);
        AssertBudget(body, 5_903, 4_879);
        body = await SendAsync(entry, Model() with { ContextWindow = 5_000 }, Context(), options);
        AssertBudget(body, 903, 1_024);
        body = await SendAsync(entry, Model() with { ContextWindow = 4_096 }, Context(), options);
        AssertBudget(body, 1, 1_024);
        var computed = SimpleTokenOptions.ResolveAnthropic(Model() with { ContextWindow = 5_000 }, Context(), 2_048, 16_384);
        Assert.Equal(0, computed.ThinkingBudget);
    }

    /// <summary>关闭思考和自适应思考仅裁剪输出空间，不追加固定预算。</summary>
    /// <param name="entry">公开调用入口。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("direct")]
    [InlineData("simple")]
    [InlineData("configured")]
    [InlineData("models")]
    public async Task OffAndAdaptive_UseOnlyBaseBudget(string entry)
    {
        foreach (var level in new ThinkingLevel?[] { null, ThinkingLevel.Off })
        {
            var body = await SendAsync(entry, Model(), Context(), new() { Reasoning = level });
            Assert.Equal(5_903, body.GetProperty("max_tokens").GetInt32());
            Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
        }
        var adaptive = await SendAsync(entry, Model() with { Compat = new() { ForceAdaptiveThinking = true } }, Context(),
            new() { Reasoning = ThinkingLevel.High, MaxTokens = 2_048 });
        Assert.Equal(2_048, adaptive.GetProperty("max_tokens").GetInt32());
        Assert.Equal("adaptive", adaptive.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(adaptive.GetProperty("thinking").TryGetProperty("budget_tokens", out _));
    }

    /// <summary>上下文估算发生在系统消息折叠和非视觉图片降级之前，旧顶层字段也计入。</summary>
    /// <param name="entry">公开调用入口。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("direct")]
    [InlineData("simple")]
    [InlineData("configured")]
    [InlineData("models")]
    public async Task ContextEstimate_UsesOriginalTranscript(string entry)
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var context = new LlmContext("base", [
            new UserMessage([new ImageContent("base64", "image/png")]),
            new SystemMessage("update") { ToolsRemoved = [new("read")] }, new UserMessage("hi")],
            [new Tool("read", "Read", schema.RootElement.Clone())]);
        var body = await SendAsync(entry, Model(), context, new());
        // 1. 【AI】【上下文估算回归】上游实算：1 正文 + 18 工具 + 1200 图片 + 2 更新 + 5 移除 + 1 尾部
        Assert.Equal(4_677, body.GetProperty("max_tokens").GetInt32());
        Assert.False(body.TryGetProperty("tools", out _));
        Assert.DoesNotContain("\"type\":\"image\"", body.GetRawText());
        Assert.Single(context.Tools!);
        Assert.IsType<ImageContent>(((UserMessage)context.Messages[0]).Content[0]);
    }

    /// <summary>新回复用量决定剩余空间；插入较新摘要后保留的旧回复用量必须失效。</summary>
    /// <param name="entry">公开调用入口。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("direct")]
    [InlineData("simple")]
    [InlineData("configured")]
    [InlineData("models")]
    public async Task Usage_RespectsCompactedPrefix(string entry)
    {
        var context = new LlmContext(null, [
            new UserMessage("summary") { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(200) },
            new AssistantMessage([new TextContent("kept")])
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(100), Usage = new(9_500, 0),
                Api = "anthropic-messages", Provider = "test", Model = "budget-model"
            },
            new UserMessage("tail") { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(300) }], null);
        var body = await SendAsync(entry, Model(), context, new());
        Assert.Equal(5_900, body.GetProperty("max_tokens").GetInt32());
        context = context with { Messages = [.. context.Messages,
            new AssistantMessage { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(400), Usage = new(2_000, 0) },
            new UserMessage("tail") { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(500) }] };
        body = await SendAsync(entry, Model(), context, new());
        Assert.Equal(3_903, body.GetProperty("max_tokens").GetInt32());
    }

    /// <summary>无思考时显式预算只受上下文约束；非推理模型与托管 effort 保持上游的计算顺序。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Budget_PreservesUpstreamModelCapabilityOrdering()
    {
        var body = await SendAsync("direct", Model() with { ContextWindow = 0, MaxOutputTokens = 4_096 }, Context(), new() { MaxTokens = 20_000 });
        Assert.Equal(20_000, body.GetProperty("max_tokens").GetInt32());
        body = await SendAsync("direct", Model() with { Reasoning = false }, Context(), new() { Reasoning = ThinkingLevel.High, MaxTokens = 2_048 });
        Assert.Equal(5_903, body.GetProperty("max_tokens").GetInt32());
        Assert.False(body.TryGetProperty("thinking", out _));
        body = await SendAsync("direct", Model() with { Compat = new() { SupportsMidConvoEffort = true } }, Context(), new() { Reasoning = ThinkingLevel.High, MaxTokens = 2_048 });
        Assert.Equal(5_903, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("adaptive", body.GetProperty("thinking").GetProperty("type").GetString());
    }

    /// <summary>高级入口尊重原始 token 选项，不能重复追加思考或自动裁剪。</summary>
    /// <param name="entry">高级入口名称。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("advanced")]
    [InlineData("advanced-configured")]
    public async Task AdvancedStream_PreservesExplicitBudgets(string entry)
    {
        var body = await SendAsync(entry, Model() with { ContextWindow = 4_096 }, Context(), new() { MaxTokens = 2_048 });
        AssertBudget(body, 2_048, 7_000);
    }

    /// <summary>检查实际请求中的总上限与固定思考预算。</summary>
    /// <param name="body">捕获的请求。</param>
    /// <param name="maxTokens">预期总上限。</param>
    /// <param name="thinkingBudget">预期思考预算。</param>
    private static void AssertBudget(JsonElement body, int maxTokens, int thinkingBudget)
    {
        Assert.Equal(maxTokens, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(thinkingBudget, body.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
    }

    /// <summary>创建统一测试模型。</summary>
    /// <returns>支持固定预算思考的模型。</returns>
    private static Model Model() => new() { Id = "budget-model", Name = "budget", Provider = "test", Api = "anthropic-messages", Reasoning = true, ContextWindow = 10_000, MaxOutputTokens = 8_000 };

    /// <summary>创建成本为一个 token 的简单上下文。</summary>
    /// <returns>用户消息上下文。</returns>
    private static LlmContext Context() => new(null, [new UserMessage("hi")], null);

    /// <summary>通过真实 provider 和内存 HTTP 处理器捕获请求，配置入口显式启用专用选项转换。</summary>
    /// <param name="entry">入口名称。</param>
    /// <param name="model">测试模型。</param>
    /// <param name="context">输入会话。</param>
    /// <param name="options">简化选项或高级入口的基础选项。</param>
    /// <returns>独立持有内存的请求 JSON。</returns>
    private static async Task<JsonElement> SendAsync(string entry, Model model, LlmContext context, SimpleStreamOptions options)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(Success));
        using var client = new HttpClient(handler);
        IStreamProvider provider = new AnthropicProvider(client);
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var path = Path.Combine(Path.GetTempPath(), "tau-budget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // 1. 【AI】【Anthropic 预算回归】只设置显示选项，确保配置转换不改变预算输入
            File.WriteAllText(path, """{"providers":{"test":{"options":{"thinkingDisplay":"summarized"},"models":[{"id":"budget-model"}]}}}""");
            var store = new ModelConfigurationStore(entry.Contains("configured", StringComparison.Ordinal) ? [path] : []);
            options = options with { ApiKey = "test-key", Env = new Dictionary<string, string> { ["ANTHROPIC_AUTH_TOKEN"] = "", ["ANTHROPIC_OAUTH_TOKEN"] = "", ["ANTHROPIC_API_KEY"] = "" } };
            var advanced = new AnthropicOptions { MaxTokens = options.MaxTokens, ApiKey = options.ApiKey, Env = options.Env, ThinkingEnabled = true, ThinkingBudgetTokens = 7_000 };
            var models = new Models([new ProviderDefinition(model.Provider, provider, [model])]);
            var stream = entry switch
            {
                "direct" => provider.StreamSimple(model, context, options),
                "models" => models.StreamSimple(model, context, options),
                "advanced" => provider.Stream(model, context, advanced),
                "advanced-configured" => StreamFunctions.Stream(registry, model, context, advanced, store),
                _ => StreamFunctions.StreamSimple(registry, model, context, options, store)
            };

            // 2. 【AI】【Anthropic 预算回归】等待真实请求完成并确认未被错误事件提前中断
            var result = await stream.ResultAsync;
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Single(handler.Requests);
            using var document = JsonDocument.Parse(handler.CapturedBody!);
            return document.RootElement.Clone();
        }
        finally { File.Delete(path); }
    }
}
