// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.Bedrock;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers.Mistral;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

/// <summary>【AI】【上下文预算】检查各提供方简化入口在实际请求中预留上下文安全空间。</summary>
[Collection("BedrockEnvironment")]
public sealed class SimpleContextBudgetTests
{
    /// <summary>【AI】【历史用量】实际请求预算使用有效 usage 与尾部消息；重排后过期 usage 不覆盖新上下文。</summary>
    /// <param name="validUsage">usage 是否晚于系统消息。</param><param name="usageTokens">报告总量。</param>
    /// <param name="expected">预期输出上限。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true, 2000, 3903)]
    [InlineData(true, 9900, 1)]
    [InlineData(false, 9900, 5901)]
    public async Task RequestBudgetUsesOnlyApplicableUsage(bool validUsage, int usageTokens, int expected)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("stop") });
        using var client = new HttpClient(handler);
        var model = new Model
        {
            Id = "test", Name = "Test", Provider = "local", Api = "openai-completions",
            BaseUrl = "https://example.invalid/v1", ContextWindow = 10000, MaxOutputTokens = 8000
        };
        var context = new LlmContext
        {
            Messages =
            [
                new SystemMessage("abcd") { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000) },
                new AssistantMessage([new TextContent("1234")])
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(validUsage ? 2000 : 0),
                    Usage = new(usageTokens, 0) { TotalTokens = usageTokens }, StopReason = StopReason.EndTurn
                },
                new UserMessage("efgh") { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(3000) }
            ]
        };
        await OpenAiResponsesProviderTests.CollectAsync(new OpenAiProvider(client).StreamSimple(model, context,
            new() { ApiKey = "synthetic", MaxRetries = 0 }));
        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        Assert.Equal(expected, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    /// <summary>【AI】【预算矩阵】覆盖默认值、显式值、图片估算和原生入口边界。</summary>
    /// <returns>协议、输出上限、是否包含图片、是否简化入口及预期预算。</returns>
    public static TheoryData<string, int?, bool, bool, int> BudgetCases()
    {
        var data = new TheoryData<string, int?, bool, bool, int>();
        foreach (var api in new[] { "openai", "compatible", "responses", "azure", "codex", "anthropic", "bedrock", "google", "vertex", "mistral" })
        {
            data.Add(api, null, false, true, 4902);
            data.Add(api, 10000, false, true, 4902);
            data.Add(api, 100, false, true, 100);
            data.Add(api, 10000, true, true, 3702);
            data.Add(api, 10000, false, false, 10000);
        }
        return data;
    }

    /// <summary>【AI】【HTTP预算】依据原始系统提示及图片估算进行裁剪，普通入口保留显式原生上限。</summary>
    /// <param name="api">提供方实现。</param><param name="maxTokens">请求上限。</param><param name="image">是否携带图片。</param>
    /// <param name="simple">是否使用简化入口。</param><param name="expected">发送上限。</param><returns>异步测试任务。</returns>
    [Theory]
    [MemberData(nameof(BudgetCases))]
    public async Task RequestBudgetUsesOriginalContext(string api, int? maxTokens, bool image, bool simple, int expected)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("stop") });
        using var client = new HttpClient(handler);
        IStreamProvider provider = api switch
        {
            "openai" => new OpenAiProvider(client), "compatible" => new OpenAiCompatibleProvider("compatible", "https://example.invalid/v1", httpClient: client),
            "responses" => new OpenAiResponsesProvider(client), "azure" => new AzureOpenAiResponsesProvider(client),
            "codex" => new OpenAiCodexResponsesProvider(client), "anthropic" => new AnthropicProvider(client),
            "bedrock" => new BedrockProvider(client), "google" => new GoogleProvider(client),
            "vertex" => new GoogleVertexProvider(client), _ => new MistralProvider(client)
        };
        var model = new Model
        {
            Id = "test-model", Name = "Test", Provider = api, Api = provider.Api,
            BaseUrl = api == "bedrock" ? "https://bedrock-runtime.us-east-1.amazonaws.com" : "https://example.invalid/v1",
            ContextWindow = 9000, MaxOutputTokens = 8192, InputModalities = ["text"]
        };
        var context = new LlmContext
        {
            SystemPrompt = "abcd",
            Messages = [new UserMessage(image ? [new TextContent("efgh"), new ImageContent("dGVzdA==", "image/png")] : [new TextContent("efgh")])]
        };
        var options = new SimpleStreamOptions
        {
            ApiKey = api == "codex" ? OpenAiResponsesSharedTests.BuildFakeJwt("synthetic-context") : "synthetic",
            MaxTokens = maxTokens, MaxRetries = 0, Transport = StreamTransport.Sse,
            Env = new Dictionary<string, string> { ["AWS_REGION"] = "us-east-1", ["AWS_BEDROCK_SKIP_AUTH"] = "0" }
        };
        var events = await OpenAiResponsesProviderTests.CollectAsync(simple
            ? provider.StreamSimple(model, context, options) : provider.Stream(model, context, options));
        Assert.True(handler.Requests.Count == 1, string.Join("; ", events.OfType<ErrorEvent>().Select(item => item.Error)));
        using var body = JsonDocument.Parse(handler.CapturedBody);
        var root = body.RootElement;
        var actual = api switch
        {
            "google" or "vertex" => root.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32(),
            "bedrock" => root.GetProperty("inferenceConfig").GetProperty("maxTokens").GetInt32(),
            "responses" or "azure" or "codex" => root.GetProperty("max_output_tokens").GetInt32(),
            "anthropic" or "mistral" => root.GetProperty("max_tokens").GetInt32(),
            _ => root.GetProperty("max_completion_tokens").GetInt32()
        };
        Assert.Equal(expected, actual);
        Assert.Equal(maxTokens, options.MaxTokens);
        Assert.Equal("abcd", context.SystemPrompt);
        Assert.Single(context.Messages);
    }
}
