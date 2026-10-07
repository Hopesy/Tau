// 作者：xxx
using System.Text.Json;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed partial class ModelConfigurationValidationTests
{
    /// <summary>【AI】【预算配置链】专用配置转换保留调用方预算，关闭思考不会回退为 high。</summary>
    /// <param name="enabled">是否请求启用思考。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfiguredSimpleRequestPreservesBudgetAndOff(bool enabled)
    {
        using var fixture = new ConfigurationFixture("""
            {"providers":{"local":{"api":"openai-completions","baseUrl":"https://example.invalid/v1",
              "compat":{"thinkingFormat":"chat-template","supportsThinkingTokenBudget":true,
                "chatTemplateKwargs":{"enabled":{"$var":"thinking.enabled"}}},
              "models":[{"id":"template","reasoning":true,"maxTokens":8000,"contextWindow":10000,
                "options":{"reasoningEffort":"low"}}]}}}
            """);
        var model = new ModelCatalog(configurationStore: fixture.Store).GetModel("local", "template");
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("stop") });
        using var client = new HttpClient(handler);
        var registry = new Tau.Ai.Providers.ProviderRegistry();
        registry.Register("openai-chat-completions", () => new Tau.Ai.Providers.OpenAi.OpenAiProvider(client), "test");
        var original = new SimpleStreamOptions
        {
            ApiKey = "synthetic", Reasoning = enabled ? ThinkingLevel.High : ThinkingLevel.Off,
            ThinkingBudgets = new() { High = 37 }, MaxRetries = 0
        };
        var prepared = false;
        await OpenAiResponsesProviderTests.CollectAsync(Tau.Ai.Providers.StreamFunctions.StreamSimple(registry, model,
            new() { Messages = [new UserMessage("abcd")] }, original, fixture.Store,
            onRequestPrepared: (_, _, simple, native) =>
            {
                prepared = true;
                Assert.Equal(5903, simple.MaxTokens);
                var options = Assert.IsType<Tau.Ai.Providers.OpenAi.OpenAiOptions>(native);
                Assert.Equal(5903, options.MaxTokens);
                Assert.Equal(37, options.ThinkingBudgets!.High);
                Assert.Equal(enabled ? "high" : null, options.ReasoningEffort);
            }));
        Assert.True(prepared);
        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        Assert.Equal(5903, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(enabled, body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enabled").GetBoolean());
        Assert.Equal(enabled, body.RootElement.TryGetProperty("thinking_token_budget", out var budget));
        if (enabled) Assert.Equal(37, budget.GetInt32());
        Assert.Null(original.MaxTokens);
    }

    /// <summary>【AI】【模板配置链】从文件继承模板、覆盖键、复制目录并实际发送，检查大小写与预算元数据。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ConfiguredTemplatesReachRequestWithoutLosingKeys()
    {
        using var fixture = new ConfigurationFixture("""
            {"providers":{"local":{"api":"openai-completions","baseUrl":"https://example.invalid/v1",
              "compat":{"thinkingFormat":"chat-template","supportsThinkingTokenBudget":true,
                "chatTemplateKwargs":{"Case":"upper","case":"lower","shared":"provider","enabled":{"$var":"thinking.enabled"}}},
              "models":[{"id":"template","reasoning":true,"maxTokens":5000,
                "compat":{"chatTemplateKwargs":{"shared":"model","extra":null}}}]}}}
            """);
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        Assert.Null(fixture.Store.Error);
        var model = catalog.CreateSessionCopy().GetModel("local", "template");
        Assert.True(model.Compat!.SupportsThinkingTokenBudget);
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload")
        });
        using var client = new HttpClient(handler);
        var provider = new Tau.Ai.Providers.OpenAi.OpenAiProvider(client);
        await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(model, new() { Messages = [new UserMessage("hi")] },
            new() { ApiKey = "synthetic-test", MaxRetries = 0, Reasoning = ThinkingLevel.High }));
        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        var template = body.RootElement.GetProperty("chat_template_kwargs");
        Assert.Equal("upper", template.GetProperty("Case").GetString());
        Assert.Equal("lower", template.GetProperty("case").GetString());
        Assert.Equal("model", template.GetProperty("shared").GetString());
        Assert.Equal(JsonValueKind.Null, template.GetProperty("extra").ValueKind);
        Assert.True(template.GetProperty("enabled").GetBoolean());
        Assert.Equal(3976, body.RootElement.GetProperty("thinking_token_budget").GetInt32());
    }

    /// <summary>【AI】【路由元数据】验证亲和格式和零优先级覆盖后仍能通过目录复制传递。</summary>
    [Fact]
    public void AffinityAndPrioritySurviveConfigurationLayers()
    {
        using var fixture = new ConfigurationFixture("""
            {"providers":{"local":{"compat":{"sessionAffinityFormat":"openrouter","vllmPriority":0.5},
              "models":[{"id":"inherited"},{"id":"overridden","compat":{"sessionAffinityFormat":"openai-nosession","vllmPriority":0}}]}}}
            """);
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        foreach (var view in new[] { catalog, catalog.CreateSessionCopy() })
        {
            Assert.Equal("openrouter", view.GetModel("local", "inherited").Compat!.SessionAffinityFormat);
            Assert.Equal(0.5, view.GetModel("local", "inherited").Compat!.VllmPriority);
            Assert.Equal("openai-nosession", view.GetModel("local", "overridden").Compat!.SessionAffinityFormat);
            Assert.Equal(0, view.GetModel("local", "overridden").Compat!.VllmPriority);
        }
    }

    /// <summary>【AI】【兼容联合】只有三个候选对象均不匹配时才拒绝，未知字段按原生联合规则保留。</summary>
    /// <param name="compat">兼容 JSON。</param><param name="valid">是否至少匹配一个协议。</param>
    [Theory]
    [InlineData("""{"supportsLongCacheRetention":false}""", true)]
    [InlineData("""{"supportsLongCacheRetention":"false"}""", false)]
    [InlineData("""{"supportsDeveloperRole":"unknown-to-anthropic"}""", true)]
    [InlineData("""{"supportsTemperature":"unknown-to-responses"}""", true)]
    [InlineData("""{"supportsDeveloperRole":123,"supportsTemperature":123}""", false)]
    [InlineData("""{"future":{"arbitrary":[null,true,1]}}""", true)]
    public void CompatibilityUnionRequiresOneValidBranch(string compat, bool valid)
    {
        using var fixture = new ConfigurationFixture("""{"providers":{"local":{"compat":""" + compat + ""","models":[{"id":"model"}]}}}""");
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        Assert.Equal(valid, catalog.GetModels("local").Count > 0);
        if (valid) Assert.Null(fixture.Store.Error);
        else Assert.Contains("providers.local.compat", fixture.Store.Error);
    }

    /// <summary>【AI】【补全兼容细节】排除其他联合分支后验证路由、模板与格式字段，错误包含实际嵌套路径。</summary>
    /// <param name="fields">无效补全配置字段。</param><param name="path">预期路径片段。</param>
    [Theory]
    [InlineData("""" "maxTokensField":"invalid" """", "maxTokensField")]
    [InlineData("""" "thinkingFormat":"invalid" """", "thinkingFormat")]
    [InlineData("""" "cacheControlFormat":"invalid" """", "cacheControlFormat")]
    [InlineData("""" "sessionAffinityFormat":null """", "sessionAffinityFormat")]
    [InlineData("""" "vllmPriority":"1" """", "vllmPriority")]
    [InlineData("""" "chatTemplateArgs":{"bad":[]} """", "chatTemplateArgs.bad")]
    [InlineData("""" "chatTemplateKwargs":{"bad":{}} """", "chatTemplateKwargs.bad.$var")]
    [InlineData("""" "chatTemplateKwargs":{"bad":{"$var":"invalid"}} """", "chatTemplateKwargs.bad.$var")]
    [InlineData("""" "chatTemplateKwargs":{"bad":{"$var":"thinking.enabled","omitWhenOff":"true"}} """", "chatTemplateKwargs.bad.omitWhenOff")]
    [InlineData("""" "vercelGatewayRouting":{"order":[1]} """", "vercelGatewayRouting.order")]
    [InlineData("""" "openRouterRouting":{"only":true} """", "openRouterRouting.only")]
    [InlineData("""" "openRouterRouting":{"sort":{"partition":false}} """", "openRouterRouting.sort.partition")]
    [InlineData("""" "openRouterRouting":{"max_price":{"prompt":null}} """", "openRouterRouting.max_price.prompt")]
    [InlineData("""" "openRouterRouting":{"data_collection":"unknown"} """", "openRouterRouting.data_collection")]
    [InlineData("""" "openRouterRouting":{"preferred_min_throughput":{"p50":"10"}} """", "openRouterRouting.preferred_min_throughput.p50")]
    [InlineData("""" "openRouterRouting":{"preferred_max_latency":[]} """", "openRouterRouting.preferred_max_latency")]
    public void CompletionsCompatibilityReportsNestedFields(string fields, string path)
    {
        using var fixture = new ConfigurationFixture(
            """{"providers":{"local":{"models":[{"id":"model","compat":{"supportsMaxOutputTokens":"invalid","supportsTemperature":"invalid",""" + fields + "}}]}}}");
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        Assert.Empty(catalog.GetModels("local"));
        Assert.Contains("providers.local.models.0.compat." + path, fixture.Store.Error);
    }

    /// <summary>【AI】【合法兼容嵌套】完整路由和模板配置允许空字符串、空数组、标量与思考变量。</summary>
    [Fact]
    public void CompletionsCompatibilityAcceptsNativeNestedValues()
    {
        using var fixture = new ConfigurationFixture("""
            {"providers":{"local":{"compat":{
              "supportsMaxOutputTokens":"not-a-completions-field","supportsTemperature":"not-a-completions-field",
              "maxTokensField":"max_tokens","sessionAffinityFormat":"openai-nosession","vllmPriority":0.5,
              "chatTemplateKwargs":{"string":"","number":1,"boolean":true,"null":null,
                "enabled":{"$var":"thinking.enabled","omitWhenOff":true},"effort":{"$var":"thinking.effort"}},
              "chatTemplateArgs":{"mode":"literal"},
              "vercelGatewayRouting":{"only":[],"order":["provider"]},
              "openRouterRouting":{"allow_fallbacks":false,"require_parameters":true,"zdr":true,"enforce_distillable_text":false,
                "data_collection":"deny","order":["provider"],"ignore":[],"quantizations":["fp16"],
                "sort":{"by":"price","partition":null},"max_price":{"prompt":"1.2","completion":0.5},
                "preferred_min_throughput":{"p50":50,"p99":10},"preferred_max_latency":1.5}
              },"models":[{"id":"model"}]}}}
            """);
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        Assert.Null(fixture.Store.Error);
        Assert.Single(catalog.GetModels("local"));
    }

    /// <summary>【AI】【回退兼容】完整回退对象要求提供方、模型、费用，超过三项或缺少必需字段时拒绝。</summary>
    /// <param name="fallbacks">回退模型数组。</param><param name="path">预期路径片段。</param>
    [Theory]
    [InlineData("[{},{},{},{}]", "allowedFallbackModels")]
    [InlineData("[{}]", "allowedFallbackModels.0.provider")]
    [InlineData("""[{"provider":"","model":"m","cost":{}}]""", "allowedFallbackModels.0.provider")]
    [InlineData("""[{"provider":"p","model":"m","cost":{"input":1}}]""", "allowedFallbackModels.0.cost.output")]
    public void AnthropicCompatibilityValidatesFallbackModels(string fallbacks, string path)
    {
        using var fixture = new ConfigurationFixture(
            """{"providers":{"local":{"compat":{"supportsDeveloperRole":"invalid","allowedFallbackModels":""" + fallbacks + """},"models":[{"id":"model"}]}}}""");
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        Assert.Empty(catalog.GetModels("local"));
        Assert.Contains(path, fixture.Store.Error);
    }

    /// <summary>【AI】【兼容读取】已声明的兼容字段从文件读入、按层覆盖并在会话目录复制后保留。</summary>
    [Fact]
    public void CompatibilityFieldsSurviveLayeringAndSessionCopy()
    {
        using var fixture = new ConfigurationFixture("""
            {"providers":{"local":{"compat":{
              "supportsExplicitPromptCacheMode":true,"supportsMaxOutputTokens":false,"supportsToolReferences":true,
              "supportsDeferredTools":true,"thinkingTokenBudgetField":"budget","chatTemplateArgs":{"base":1,"shared":"provider"}
              },"models":[{"id":"model","compat":{"supportsDeferredTools":false,"chatTemplateArgs":{"shared":"model","extra":true}}}]}}}
            """);
        var catalog = new ModelCatalog(configurationStore: fixture.Store);
        foreach (var model in new[] { catalog.GetModel("local", "model"), catalog.CreateSessionCopy().GetModel("local", "model") })
        {
            var compat = model!.Compat!;
            Assert.True(compat.SupportsExplicitPromptCacheMode);
            Assert.False(compat.SupportsMaxOutputTokens);
            Assert.True(compat.SupportsToolReferences);
            Assert.False(compat.SupportsDeferredTools);
            Assert.Equal("budget", compat.ThinkingTokenBudgetField);
            Assert.Equal(1, Assert.IsType<JsonElement>(compat.ChatTemplateArgs!["base"]).GetInt32());
            Assert.Equal("model", Assert.IsType<JsonElement>(compat.ChatTemplateArgs["shared"]).GetString());
            Assert.True(Assert.IsType<JsonElement>(compat.ChatTemplateArgs["extra"]).GetBoolean());
        }
    }
}
