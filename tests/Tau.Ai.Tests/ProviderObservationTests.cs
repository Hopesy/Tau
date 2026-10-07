// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers.Mistral;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Providers.PiMessages;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【协议观察回归】验证各协议的请求头转换与原始流事件观察器。</summary>
public sealed class ProviderObservationTests
{
    /// <summary>实际协议及公开选项转换入口的测试矩阵。</summary>
    public static TheoryData<string, string> Protocols
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var api in new[] { "chat", "compatible", "responses", "azure", "codex", "anthropic", "google", "vertex", "gemini-cli", "mistral", "pi" })
                foreach (var entry in new[] { "simple", "models" }) cases.Add(api, entry);
            return cases;
        }
    }

    /// <summary>头部可新增、覆盖、删除，原始 JSON 观察器接收实际模型及归一化前事件。</summary>
    /// <param name="api">协议别名。</param>
    /// <param name="entry">简化入口或统一模型入口。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Hooks_SurviveOptionsConversionAndObserveActualTransport(string api, string entry)
    {
        var headersCalled = 0;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            Assert.Equal(1, headersCalled);
            Assert.Equal("changed", request.Headers.GetValues("X-Synthetic").Single());
            Assert.False(request.Headers.Contains("X-Remove"));
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            return OpenAiResponsesProviderTests.SseResponse(Response(api));
        });
        using var client = new HttpClient(handler);
        var provider = Provider(api, client);
        using var lifetime = provider as IDisposable;
        var model = new Model { Id = "synthetic", Name = "Synthetic", Provider = "test", Api = provider.Api, BaseUrl = "https://synthetic.invalid",
            Headers = new Dictionary<string, string> { ["X-Synthetic"] = "old", ["X-Remove"] = "remove" } };
        var seen = new List<JsonElement>();
        var options = new SimpleStreamOptions
        {
            ApiKey = api == "codex" ? OpenAiResponsesSharedTests.BuildFakeJwt("synthetic") :
                api == "gemini-cli" ? """{"token":"synthetic","projectId":"test"}""" : "synthetic",
            Transport = StreamTransport.Sse,
            Env = new Dictionary<string, string> { ["GOOGLE_CLOUD_PROJECT"] = "test", ["GOOGLE_CLOUD_LOCATION"] = "us-central1" },
            TransformHeaders = (headers, actual) =>
            {
                Assert.Equal(model.Id, actual.Id);
                Assert.Equal("old", headers["x-synthetic"]);
                headers["x-synthetic"] = "changed";
                headers["x-remove"] = null;
                headersCalled++;
                return ValueTask.FromResult<IDictionary<string, string?>?>(null);
            },
            OnProviderStreamEvent = async (data, actual) =>
            {
                Assert.Equal(model.Id, actual.Id);
                await Task.Yield();
                seen.Add(data);
            }
        };
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var models = new Models([new ProviderDefinition(model.Provider, provider, [model])]);
        var context = new LlmContext { Messages = [new UserMessage("synthetic")] };
        var stream = entry == "models" ? models.StreamSimple(model, context, options) : StreamFunctions.StreamSimple(registry, model, context, options);
        var result = await stream.ResultAsync;
        Assert.True(result.StopReason != StopReason.Error, result.ErrorMessage);
        Assert.True(headersCalled == 1, result.ErrorMessage);
        Assert.NotEmpty(seen);
        Assert.All(seen, data => Assert.Equal(JsonValueKind.Object, data.ValueKind));
        if (api == "codex") Assert.Equal("response.done", seen[^1].GetProperty("type").GetString());
        if (api == "gemini-cli") Assert.True(seen[0].TryGetProperty("response", out _));
    }

    /// <summary>回调失败返回可诊断错误，头部失败不会发送请求，事件失败不会被当成正常结束。</summary>
    /// <param name="phase">失败的回调阶段。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("headers")]
    [InlineData("stream")]
    public async Task CallbackFailure_IsNotSilentlyIgnored(string phase)
    {
        var sends = 0;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
        {
            sends++;
            return OpenAiResponsesProviderTests.SseResponse(Response("responses"));
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(client);
        var model = new Model { Id = "test", Name = "Test", Provider = "test", Api = provider.Api, BaseUrl = "https://synthetic.invalid" };
        var result = await provider.StreamSimple(model, new() { Messages = [new UserMessage("hi")] }, new()
        {
            ApiKey = "synthetic",
            TransformHeaders = phase == "headers" ? (_, _) => throw new InvalidOperationException("synthetic headers failure") : null,
            OnProviderStreamEvent = phase == "stream" ? (_, _) => throw new InvalidOperationException("synthetic stream failure") : null
        }).ResultAsync;
        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Contains("synthetic " + phase + " failure", result.ErrorMessage);
        Assert.Equal(phase == "headers" ? 0 : 1, sends);
    }

    /// <summary>根据别名创建真实协议适配器。</summary>
    /// <param name="api">协议别名。</param>
    /// <param name="client">模拟 HTTP 客户端。</param>
    /// <returns>协议提供方。</returns>
    private static IStreamProvider Provider(string api, HttpClient client) => api switch
    {
        "chat" => new OpenAiProvider(client),
        "compatible" => new OpenAiCompatibleProvider("test-compatible", "https://synthetic.invalid", httpClient: client),
        "responses" => new OpenAiResponsesProvider(client),
        "azure" => new AzureOpenAiResponsesProvider(client),
        "codex" => new OpenAiCodexResponsesProvider(client),
        "anthropic" => new AnthropicProvider(client),
        "google" => new GoogleProvider(client),
        "vertex" => new GoogleVertexProvider(client),
        "gemini-cli" => new GoogleGeminiCliProvider(client),
        "mistral" => new MistralProvider(client),
        "pi" => new PiMessagesProvider(client),
        _ => throw new ArgumentException("Unknown protocol.", nameof(api))
    };

    /// <summary>返回各协议的合成完整 SSE。</summary>
    /// <param name="api">协议别名。</param>
    /// <returns>包含正常终态的事件流。</returns>
    private static string Response(string api) => api switch
    {
        "chat" or "compatible" or "mistral" => "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
        "codex" => "data: {\"type\":\"response.done\",\"response\":{\"status\":\"completed\"}}\n\n",
        "responses" or "azure" => "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n",
        "google" or "vertex" => "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n",
        "gemini-cli" => "data: {\"response\":{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}}\n\n",
        "pi" => "data: {\"type\":\"done\",\"reason\":\"stop\",\"usage\":{\"input\":1,\"output\":1}}\n\n",
        "anthropic" => "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"synthetic\",\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\n"
            + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n"
            + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
        _ => throw new ArgumentException("Unknown protocol.", nameof(api))
    };
}
