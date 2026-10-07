// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【思考报文】验证模板变量、预算限制及两种 Completions 实现的一致性。</summary>
public sealed partial class OpenAiThinkingTemplateTests
{
    /// <summary>【AI】【模板矩阵】生成实现、入口与格式组合。</summary>
    /// <returns>是否兼容实现、是否简化入口及格式。</returns>
    public static TheoryData<bool, bool, string> TemplateCases()
    {
        var data = new TheoryData<bool, bool, string>();
        foreach (var compatible in new[] { false, true })
        foreach (var simple in new[] { false, true })
        foreach (var format in new[] { "chat-template", "baseten" }) data.Add(compatible, simple, format);
        return data;
    }

    /// <summary>【AI】【模板启用】变量在发送前解析，标量和字面 null 保留，两个参数字典独立选择。</summary>
    /// <param name="compatible">兼容实现。</param><param name="simple">简化入口。</param><param name="format">思考格式。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [MemberData(nameof(TemplateCases))]
    public async Task TemplatesResolveBindingsAndKeepLiteralValues(bool compatible, bool simple, string format)
    {
        var template = Values("""
            {"enabled":{"$var":"thinking.enabled"},"effort":{"$var":"thinking.effort"},
             "budget":{"$var":"thinking.budget"},"omittedWhenOff":{"$var":"thinking.enabled","omitWhenOff":true},
             "literalNull":null,"literalFalse":false,"literalNumber":0.5,"literalString":"",
             "Case":"upper","case":"lower"}
            """);
        var model = CreateModel(format) with
        {
            ThinkingLevelMap = new Dictionary<string, string?> { ["high"] = "mapped-high" },
            Compat = new()
            {
                ThinkingFormat = format, SupportsReasoningEffort = true,
                ChatTemplateArgs = format == "baseten" ? template : Values("""{"wrong":"args"}"""),
                ChatTemplateKwargs = format == "chat-template" ? template : Values("""{"wrong":"kwargs"}"""),
                ThinkingTokenBudgetField = "thinking_budget_tokens"
            }
        };
        using var body = await CaptureAsync(model, compatible, simple, true, 3000, 9000);
        var root = body.RootElement;
        var parameters = root.GetProperty(format == "baseten" ? "chat_template_args" : "chat_template_kwargs");
        Assert.True(parameters.GetProperty("enabled").GetBoolean());
        Assert.Equal("mapped-high", parameters.GetProperty("effort").GetString());
        Assert.Equal(1976, parameters.GetProperty("budget").GetInt32());
        Assert.True(parameters.GetProperty("omittedWhenOff").GetBoolean());
        Assert.Equal(JsonValueKind.Null, parameters.GetProperty("literalNull").ValueKind);
        Assert.False(parameters.GetProperty("literalFalse").GetBoolean());
        Assert.Equal(0.5, parameters.GetProperty("literalNumber").GetDouble());
        Assert.Equal("", parameters.GetProperty("literalString").GetString());
        Assert.Equal("upper", parameters.GetProperty("Case").GetString());
        Assert.Equal("lower", parameters.GetProperty("case").GetString());
        Assert.False(parameters.TryGetProperty("wrong", out _));
        Assert.False(parameters.TryGetProperty("enable_thinking", out _));
        Assert.Equal(1976, root.GetProperty("thinking_budget_tokens").GetInt32());
        Assert.Equal(format == "baseten", root.TryGetProperty("reasoning_effort", out var effort));
        if (format == "baseten") Assert.Equal("mapped-high", effort.GetString());
    }

    /// <summary>【AI】【模板关闭】关闭变量可省略，映射为 null 的 effort 与预算不进入请求。</summary>
    /// <param name="compatible">兼容实现。</param><param name="simple">简化入口。</param><param name="format">思考格式。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [MemberData(nameof(TemplateCases))]
    public async Task DisabledTemplatesOmitConditionalValues(bool compatible, bool simple, string format)
    {
        var model = CreateModel(format) with
        {
            ThinkingLevelMap = new Dictionary<string, string?> { ["off"] = null }
        };
        using var body = await CaptureAsync(model, compatible, simple, false, 4096);
        var parameters = body.RootElement.GetProperty(format == "baseten" ? "chat_template_args" : "chat_template_kwargs");
        Assert.False(parameters.GetProperty("enabled").GetBoolean());
        Assert.False(parameters.TryGetProperty("effort", out _));
        Assert.False(parameters.TryGetProperty("budget", out _));
        Assert.False(parameters.TryGetProperty("conditional", out _));
        Assert.False(body.RootElement.TryGetProperty("thinking_token_budget", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    /// <summary>【AI】【空模板】空声明及全部变量被省略时不发送参数对象，也不补造 Baseten 开关。</summary>
    /// <param name="compatible">兼容实现。</param><param name="simple">简化入口。</param><param name="format">思考格式。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [MemberData(nameof(TemplateCases))]
    public async Task EmptyTemplatesAreAbsent(bool compatible, bool simple, string format)
    {
        var model = CreateModel(format) with
        {
            Compat = new()
            {
                ThinkingFormat = format,
                ChatTemplateArgs = Values("""{"budget":{"$var":"thinking.budget"},"effort":{"$var":"thinking.effort","omitWhenOff":true}}"""),
                ChatTemplateKwargs = new Dictionary<string, object>()
            }
        };
        using var body = await CaptureAsync(model, compatible, simple, false, 4096);
        Assert.False(body.RootElement.TryGetProperty("chat_template_args", out _));
        Assert.False(body.RootElement.TryGetProperty("chat_template_kwargs", out _));
    }

    /// <summary>【AI】【预算边界】测试低上限、默认模型上限、自定义预算和与模板格式无关的顶层预算。</summary>
    /// <param name="compatible">兼容实现。</param><param name="maxTokens">请求上限。</param>
    /// <param name="custom">自定义高等级预算。</param><param name="expected">预期正预算或空值。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, 1024, null, null)]
    [InlineData(false, 512, null, null)]
    [InlineData(false, 4096, null, 3072)]
    [InlineData(false, null, null, 16384)]
    [InlineData(false, 4096, 12, 12)]
    [InlineData(false, 4096, 0, null)]
    [InlineData(true, 1024, null, null)]
    [InlineData(true, 512, null, null)]
    [InlineData(true, 4096, null, 3072)]
    [InlineData(true, null, null, 16384)]
    [InlineData(true, 4096, 12, 12)]
    [InlineData(true, 4096, 0, null)]
    public async Task BudgetLeavesRoomForAnswer(bool compatible, int? maxTokens, int? custom, int? expected)
    {
        using var body = await CaptureAsync(CreateModel("qwen-chat-template"), compatible, false, true, maxTokens, custom);
        Assert.Equal(expected.HasValue, body.RootElement.TryGetProperty("thinking_token_budget", out var budget));
        if (expected.HasValue) Assert.Equal(expected, budget.GetInt32());
    }

    /// <summary>【AI】【映射区别】原生请求中显式 null 抑制 effort，关闭字符串映射仍可发送。</summary>
    /// <param name="compatible">兼容实现。</param><param name="enabled">是否启用思考。</param>
    /// <param name="mapped">模型映射值。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, true, null)]
    [InlineData(false, false, "none")]
    [InlineData(true, true, null)]
    [InlineData(true, false, "none")]
    public async Task TemplateEffortDistinguishesNullAndOffMapping(bool compatible, bool enabled, string? mapped)
    {
        var model = CreateModel("baseten") with
        {
            ThinkingLevelMap = new Dictionary<string, string?> { [enabled ? "high" : "off"] = mapped }
        };
        using var body = await CaptureAsync(model, compatible, false, enabled, 4096);
        var parameters = body.RootElement.GetProperty("chat_template_args");
        Assert.Equal(enabled, parameters.GetProperty("enabled").GetBoolean());
        Assert.Equal(mapped is not null, parameters.TryGetProperty("effort", out var effort));
        Assert.Equal(mapped is not null, body.RootElement.TryGetProperty("reasoning_effort", out var nativeEffort));
        if (mapped is not null)
        {
            Assert.Equal(mapped, effort.GetString());
            Assert.Equal(mapped, nativeEffort.GetString());
        }
    }

    /// <summary>【AI】【字典变量】SDK 创建的变量字典与 JSON 变量具有相同行为。</summary>
    [Fact]
    public async Task NativeDictionaryBindingIsResolved()
    {
        var model = CreateModel("chat-template") with
        {
            Compat = new()
            {
                ThinkingFormat = "chat-template",
                ChatTemplateKwargs = new Dictionary<string, object>
                {
                    ["enabled"] = new Dictionary<string, object> { ["$var"] = "thinking.enabled" },
                    ["conditional"] = new Dictionary<string, object> { ["$var"] = "thinking.enabled", ["omitWhenOff"] = true }
                }
            }
        };
        using var body = await CaptureAsync(model, false, true, false, 4096);
        var parameters = body.RootElement.GetProperty("chat_template_kwargs");
        Assert.False(parameters.GetProperty("enabled").GetBoolean());
        Assert.False(parameters.TryGetProperty("conditional", out _));
    }

    /// <summary>【AI】【模板夹具】创建有独立 args/kwargs 和默认预算开关的思考模型。</summary>
    /// <param name="format">思考格式。</param><returns>测试模型。</returns>
    private static Model CreateModel(string format) => new()
    {
        Id = "template-model", Name = "Template model", Provider = "local", Api = "openai-completions",
        BaseUrl = "https://example.invalid/v1", Reasoning = true, MaxOutputTokens = 20000,
        Compat = new()
        {
            ThinkingFormat = format, SupportsReasoningEffort = true, SupportsThinkingTokenBudget = true,
            ChatTemplateArgs = Values("""{"enabled":{"$var":"thinking.enabled"},"effort":{"$var":"thinking.effort"},"budget":{"$var":"thinking.budget"},"conditional":{"$var":"thinking.enabled","omitWhenOff":true}}"""),
            ChatTemplateKwargs = Values("""{"enabled":{"$var":"thinking.enabled"},"effort":{"$var":"thinking.effort"},"budget":{"$var":"thinking.budget"},"conditional":{"$var":"thinking.enabled","omitWhenOff":true}}""")
        }
    };

    /// <summary>【AI】【JSON夹具】保留变量对象和字面 null，不依赖反射序列化。</summary>
    /// <param name="json">模板对象 JSON。</param><returns>拥有独立生命周期的模板字典。</returns>
    private static Dictionary<string, object> Values(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => (object)item.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>【AI】【报文捕获】调用真实提供方组装方法，传输层捕获后返回终止响应。</summary>
    /// <param name="model">目标模型。</param><param name="compatible">兼容实现。</param><param name="simple">简化入口。</param>
    /// <param name="enabled">启用思考。</param><param name="maxTokens">请求输出上限。</param><param name="budget">自定义预算。</param>
    /// <returns>由调用方释放的请求 JSON。</returns>
    private static async Task<JsonDocument> CaptureAsync(Model model, bool compatible, bool simple, bool enabled, int? maxTokens, int? budget = null)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("stop after payload") });
        using var client = new HttpClient(handler);
        IStreamProvider provider = compatible
            ? new OpenAiCompatibleProvider("compatible", "https://example.invalid/v1", httpClient: client)
            : new OpenAiProvider(client);
        var context = new LlmContext { Messages = [new UserMessage("hi")] };
        var budgets = budget.HasValue ? new ThinkingBudgets { High = budget } : null;
        var stream = simple
            ? provider.StreamSimple(model, context, new() { ApiKey = "synthetic-test", MaxRetries = 0, MaxTokens = maxTokens, Reasoning = enabled ? ThinkingLevel.High : ThinkingLevel.Off, ThinkingBudgets = budgets })
            : provider.Stream(model, context, new OpenAiOptions { ApiKey = "synthetic-test", MaxRetries = 0, MaxTokens = maxTokens, ReasoningEffort = enabled ? "high" : null, ThinkingBudgets = budgets });
        var events = await OpenAiResponsesProviderTests.CollectAsync(stream);
        Assert.True(handler.Requests.Count == 1, string.Join("; ", events.OfType<ErrorEvent>().Select(item => item.Error)));
        return JsonDocument.Parse(handler.CapturedBody);
    }
}
