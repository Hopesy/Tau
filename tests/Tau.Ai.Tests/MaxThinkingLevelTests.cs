// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

public sealed class MaxThinkingLevelTests
{
    /// <summary>【AI】【最大推理】三种真实请求构造路径保留 max，模型禁止时使用最高可用等级。</summary>
    /// <param name="api">协议。</param><param name="supportsMax">是否声明支持 max。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("openai-responses", true)]
    [InlineData("openai-responses", false)]
    [InlineData("openai-chat-completions", true)]
    [InlineData("openai-chat-completions", false)]
    [InlineData("anthropic-messages", true)]
    [InlineData("anthropic-messages", false)]
    public async Task StreamSimple_MapsMaximumThinkingPerModel(string api, bool supportsMax)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("payload captured") });
        using var client = new HttpClient(handler);
        IStreamProvider provider = api switch
        {
            "openai-responses" => new OpenAiResponsesProvider(client),
            "openai-chat-completions" => new OpenAiProvider(client),
            _ => new AnthropicProvider(client)
        };
        var model = new Model { Id = "thinking-model", Name = "Thinking", Provider = "test", Api = api, BaseUrl = "https://unit.test", Reasoning = true,
            MaxOutputTokens = 4096, ContextWindow = 100000,
            ThinkingLevelMap = new Dictionary<string, string?> { ["xhigh"] = null, ["max"] = supportsMax ? "max" : null },
            Compat = new() { ForceAdaptiveThinking = true, SupportsReasoningEffort = true } };
        await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(model, new() { Messages = [new UserMessage("think")] },
            new() { ApiKey = "test-key", Reasoning = ThinkingLevel.Max, MaxRetries = 0 }));
        using var body = JsonDocument.Parse(handler.CapturedBody);
        var effort = api switch
        {
            "openai-responses" => body.RootElement.GetProperty("reasoning").GetProperty("effort"),
            "openai-chat-completions" => body.RootElement.GetProperty("reasoning_effort"),
            _ => body.RootElement.GetProperty("output_config").GetProperty("effort")
        };
        Assert.Equal(supportsMax ? "max" : "high", effort.GetString());
    }
}
