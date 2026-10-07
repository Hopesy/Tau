// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Responses 思考回归】从真实请求体验证思考开关、等级映射和签名请求。</summary>
public sealed class ResponsesReasoningOptionsTests
{
    /// <summary>提供普通和 Azure Responses 的配置组合。</summary>
    /// <returns>协议与思考配置场景。</returns>
    public static IEnumerable<object[]> Cases()
    {
        foreach (var azure in new[] { false, true })
            foreach (var name in new[] { "off", "default", "off-disabled", "off-clamped", "off-mapped", "low-mapped", "level-disabled", "xhigh", "summary", "plain-model", "override", "copilot", "xai" })
                yield return [azure, name];
    }

    /// <summary>验证发送给服务端的思考参数，与模型能力及最后覆盖规则一致。</summary>
    /// <param name="azure">是否使用 Azure Provider。</param>
    /// <param name="name">配置场景。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Request_HonorsReasoningConfiguration(bool azure, string name)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n"));
        using var client = new HttpClient(handler);
        IStreamProvider provider = azure ? new AzureOpenAiResponsesProvider(client) : new OpenAiResponsesProvider(client);
        var map = new Dictionary<string, string?>();
        var level = name is "low-mapped" or "level-disabled" or "plain-model" ? ThinkingLevel.Low :
            name == "xhigh" ? ThinkingLevel.ExtraHigh : name is "default" or "copilot" or "xai" or "off-disabled" ? (ThinkingLevel?)null : ThinkingLevel.Off;
        if (name is "off-disabled" or "off-clamped") map["off"] = null;
        if (name == "off-mapped") map["off"] = "minimal";
        if (name == "low-mapped") map["low"] = "high";
        if (name == "level-disabled") { map["low"] = null; map["medium"] = "high"; }
        if (name == "xhigh") map["xhigh"] = "max";
        var model = new Model { Id = "reasoning", Name = "Reasoning", Api = provider.Api,
            Provider = name == "copilot" ? "github-copilot" : name == "xai" ? "xai" : "test",
            BaseUrl = "https://example.invalid/v1", Reasoning = name != "plain-model", ThinkingLevelMap = map };
        var simple = new SimpleStreamOptions { ApiKey = "key", Reasoning = level,
            SamplingParams = name == "override" ? new Dictionary<string, object> { ["reasoning"] = new Dictionary<string, object> { ["effort"] = "high" } } : null };
        var context = new LlmContext(null, [new UserMessage("synthetic")], null);
        var stream = name == "summary"
            ? provider.Stream(model, context, azure ? new AzureOpenAiResponsesOptions { ApiKey = "key", ReasoningSummary = "concise" }
                : new OpenAiResponsesOptions { ApiKey = "key", ReasoningSummary = "concise" })
            : provider.StreamSimple(model, context, simple);
        await OpenAiResponsesProviderTests.CollectAsync(stream);
        Assert.Equal(StopReason.EndTurn, (await stream.ResultAsync).StopReason);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        var root = body.RootElement;
        if (name is "off-disabled" or "plain-model" || name == "copilot" && !azure)
        {
            Assert.False(root.TryGetProperty("reasoning", out _));
            Assert.False(root.TryGetProperty("include", out _));
            return;
        }
        var expected = name switch
        {
            "off-mapped" or "off-clamped" => "minimal", "low-mapped" or "level-disabled" or "override" => "high",
            "xhigh" => "max", "summary" => "medium", _ => "none"
        };
        Assert.Equal(expected, root.GetProperty("reasoning").GetProperty("effort").GetString());
        if (name is "low-mapped" or "level-disabled" or "xhigh" or "summary" or "xai" or "off-clamped")
            Assert.Contains(root.GetProperty("include").EnumerateArray(), item => item.GetString() == "reasoning.encrypted_content");
        if (name is "low-mapped" or "level-disabled" or "xhigh" or "summary" or "off-clamped")
            Assert.Equal(name == "summary" ? "concise" : "auto", root.GetProperty("reasoning").GetProperty("summary").GetString());
    }
}
