// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;

namespace Tau.Ai.Tests;

/// <summary>【AI】【默认能力】验证缺省 compat 的自动检测及请求入口共享行为。</summary>
public sealed class OpenAiCompatibilityDetectionTests
{
    /// <summary>【AI】【路由模型】OpenRouter 的 Claude/OpenAI 模型使用 developer，其他模型保持 system。</summary>
    /// <param name="id">模型标识。</param><param name="role">系统指令角色。</param><param name="cache">是否自动添加 Anthropic 缓存控制。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("anthropic/claude", "developer", true)]
    [InlineData("openai/gpt", "developer", false)]
    [InlineData("other/model", "system", false)]
    public async Task OpenRouterModelFamilyControlsRolesAndCache(string id, string role, bool cache)
    {
        foreach (var compatible in new[] { false, true })
        {
            var model = new Model
            {
                Id = id, Name = "Router", Api = "openai-completions", Provider = "openrouter",
                BaseUrl = "https://openrouter.ai/api/v1", Reasoning = true
            };
            using var body = await CaptureAsync(model, compatible, new() { ApiKey = "synthetic", MaxRetries = 0 });
            var message = body.RootElement.GetProperty("messages")[0];
            Assert.Equal(role, message.GetProperty("role").GetString());
            if (cache)
            {
                Assert.Equal("ephemeral", message.GetProperty("content")[0].GetProperty("cache_control").GetProperty("type").GetString());
                Assert.Equal("ephemeral", body.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("cache_control").GetProperty("type").GetString());
            }
            else Assert.Equal("instructions", message.GetProperty("content").GetString());
        }
    }

    /// <summary>【AI】【流结束】兼容别名默认要求 finish_reason，明确声明不支持时才按流结束推断。</summary>
    /// <param name="supported">是否声明支持结束原因。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AliasRequiresFinishReasonUnlessDisabled(bool? supported)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse("""
            data: {"choices":[{"delta":{"content":"partial"},"finish_reason":null}]}

            data: [DONE]

            """));
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider("compatible", "https://example.invalid/v1", httpClient: client);
        var model = new Model
        {
            Id = "model", Name = "Model", Provider = "custom", Api = "openai-completions",
            Compat = new() { SupportsFinishReason = supported }
        };
        var stream = provider.Stream(model, new() { Messages = [new UserMessage("hi")] }, new() { ApiKey = "synthetic" });
        await OpenAiResponsesProviderTests.CollectAsync(stream);
        var result = await stream.ResultAsync;
        Assert.Equal(supported == false ? StopReason.EndTurn : StopReason.Error, result.StopReason);
        if (supported != false) Assert.Equal("Stream ended without finish_reason", result.ErrorMessage);
    }

    /// <summary>【AI】【检测矩阵】固定列出参考项目中不同提供方的默认请求行为。</summary>
    /// <returns>提供方、地址、store、developer、effort、max_tokens、格式及长缓存支持。</returns>
    public static TheoryData<string, string, bool, bool, bool, bool, string, bool> DetectionCases() => new()
    {
        { "openai", "https://api.openai.com/v1", true, true, true, false, "openai", true },
        { "deepseek", "https://api.DEEPSEEK.com", false, false, true, true, "deepseek", true },
        { "zai", "https://api.z.ai/v4", false, false, false, true, "zai", true },
        { "zai-coding-cn", "https://open.bigmodel.cn/api", false, false, false, true, "zai", true },
        { "together", "https://api.together.xyz/v1", false, false, false, true, "together", false },
        { "moonshotai-cn", "https://api.moonshot.cn/v1", false, false, false, true, "openai", true },
        { "xai", "https://api.x.ai/v1", false, false, false, false, "openai", true },
        { "cerebras", "https://api.cerebras.ai/v1", false, false, true, false, "openai", true },
        { "nvidia", "https://integrate.api.nvidia.com/v1", false, false, false, true, "openai", false },
        { "cloudflare-workers-ai", "https://api.cloudflare.com/client/v4", false, false, true, false, "openai", false },
        { "cloudflare-ai-gateway", "https://gateway.ai.cloudflare.com/v1/account/gateway", false, false, false, true, "openai", false },
        { "ant-ling", "https://api.ant-ling.com/v1", false, false, false, true, "ant-ling", false },
        { "opencode", "https://opencode.ai/zen/v1", false, false, true, false, "openai", true },
        { "openrouter", "https://openrouter.ai/api/v1", true, true, true, false, "openrouter", true },
        { "custom-chutes", "https://llm.chutes.ai/v1", false, false, true, true, "openai", true }
    };

    /// <summary>【AI】【检测报文】原生与别名实现分别验证名称检测、URL 检测，并检查调用方模型未被填充。</summary>
    /// <param name="provider">提供方名。</param><param name="url">特征地址。</param><param name="store">store 支持。</param>
    /// <param name="developer">developer 支持。</param><param name="effort">effort 支持。</param><param name="maxTokens">是否使用 max_tokens。</param>
    /// <param name="format">默认思考格式。</param><param name="longCache">是否支持长缓存。</param><returns>异步测试任务。</returns>
    [Theory]
    [MemberData(nameof(DetectionCases))]
    public async Task AutoDetectionReachesBothRequestImplementations(string provider, string url, bool store, bool developer,
        bool effort, bool maxTokens, string format, bool longCache)
    {
        foreach (var compatible in new[] { false, true })
        foreach (var byUrl in new[] { false, true })
        {
            var model = new Model
            {
                Id = "openai/test", Name = "Test", Api = "openai-completions", Reasoning = true, MaxOutputTokens = 8192,
                Provider = byUrl ? "local-proxy" : provider,
                BaseUrl = byUrl || provider == "custom-chutes" ? url : "https://example.invalid/v1",
                ThinkingLevelMap = new Dictionary<string, string?> { ["high"] = "mapped" }
            };
            using var body = await CaptureAsync(model, compatible, new OpenAiOptions
            {
                ApiKey = "synthetic", ReasoningEffort = "high", MaxTokens = 4096,
                CacheRetention = CacheRetention.Long, SessionId = "session", MaxRetries = 0
            });
            var root = body.RootElement;
            Assert.Equal(store, root.TryGetProperty("store", out var stored));
            if (store) Assert.False(stored.GetBoolean());
            Assert.Equal(developer ? "developer" : "system", root.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal(4096, root.GetProperty(maxTokens ? "max_tokens" : "max_completion_tokens").GetInt32());
            Assert.Equal(longCache, root.TryGetProperty("prompt_cache_retention", out _));
            Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            Assert.Equal(effort && format is not ("openrouter" or "ant-ling"), root.TryGetProperty("reasoning_effort", out var level));
            if (effort && format is not ("openrouter" or "ant-ling")) Assert.Equal("mapped", level.GetString());
            if (format is "deepseek" or "zai") Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
            if (format is "openrouter" or "ant-ling") Assert.Equal("mapped", root.GetProperty("reasoning").GetProperty("effort").GetString());
            if (format == "together") Assert.True(root.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
            Assert.Null(model.Compat);
        }
    }

    /// <summary>【AI】【显式覆盖】不标准网关可逐项覆盖自动检测，回调仍收到原模型对象。</summary>
    /// <param name="compatible">是否使用别名实现。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCompatibilityOverridesDetection(bool compatible)
    {
        var model = new Model
        {
            Id = "model", Name = "Model", Api = "openai-completions", Provider = "nvidia",
            BaseUrl = "https://integrate.api.nvidia.com/v1", Reasoning = true,
            Compat = new()
            {
                SupportsStore = true, SupportsDeveloperRole = true, SupportsReasoningEffort = true,
                SupportsUsageInStreaming = false, SupportsTemperature = false, SupportsLongCacheRetention = true,
                MaxTokensField = "max_completion_tokens", ThinkingFormat = "qwen"
            }
        };
        var observed = false;
        using var body = await CaptureAsync(model, compatible, new OpenAiOptions
        {
            ApiKey = "synthetic", MaxRetries = 0, MaxTokens = 1234, Temperature = 0.5f, ReasoningEffort = "low",
            CacheRetention = CacheRetention.Long, SessionId = "s",
            OnPayload = (payload, actualModel) =>
            {
                Assert.Same(model, actualModel);
                observed = true;
                return ValueTask.FromResult<object?>(null);
            }
        });
        Assert.True(observed);
        var root = body.RootElement;
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("developer", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("low", root.GetProperty("reasoning_effort").GetString());
        Assert.True(root.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(1234, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("24h", root.GetProperty("prompt_cache_retention").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("stream_options", out _));
    }

    /// <summary>【AI】【代理路由】显式网关路由允许自定义代理地址和空数组。</summary>
    /// <param name="compatible">是否使用别名实现。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRoutingWorksThroughCustomProxy(bool compatible)
    {
        var model = new Model
        {
            Id = "model", Name = "Model", Provider = "proxy", Api = "openai-completions", BaseUrl = "https://proxy.invalid/v1",
            Compat = new()
            {
                OpenRouterRouting = new Dictionary<string, object> { ["order"] = new[] { "one", "two" }, ["allow_fallbacks"] = false },
                VercelGatewayRouting = new() { Only = [], Order = ["one"] }
            }
        };
        using var body = await CaptureAsync(model, compatible, new() { ApiKey = "synthetic", MaxRetries = 0 });
        Assert.False(body.RootElement.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean());
        Assert.Equal("two", body.RootElement.GetProperty("provider").GetProperty("order")[1].GetString());
        var gateway = body.RootElement.GetProperty("providerOptions").GetProperty("gateway");
        Assert.Empty(gateway.GetProperty("only").EnumerateArray());
        Assert.Equal("one", gateway.GetProperty("order")[0].GetString());
    }

    /// <summary>【AI】【报文捕获】经实际提供方发送并在传输层截获 JSON。</summary>
    /// <param name="model">模型。</param><param name="compatible">别名实现。</param><param name="options">请求选项。</param>
    /// <returns>由调用方释放的请求 JSON。</returns>
    private static async Task<JsonDocument> CaptureAsync(Model model, bool compatible, StreamOptions options)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("stop") });
        using var client = new HttpClient(handler);
        IStreamProvider provider = compatible ? new OpenAiCompatibleProvider("compatible", "https://example.invalid/v1", httpClient: client) : new OpenAiProvider(client);
        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(model,
            new() { SystemPrompt = "instructions", Messages = [new UserMessage("hi")] }, options));
        Assert.True(handler.Requests.Count == 1, string.Join("; ", events.OfType<ErrorEvent>().Select(item => item.Error)));
        return JsonDocument.Parse(handler.CapturedBody);
    }
}
