// 作者：xxx
using System.Net;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

/// <summary>【AI】【SDK 重试回归】五种主线 HTTP 入口保留请求配置并区分可重试传输失败。</summary>
public sealed class ProviderHttpRetryTests
{
    /// <summary>【AI】【HTTP 状态】服务端错误可按显式次数恢复，认证错误及默认零次数不重试。</summary>
    /// <param name="api">协议别名。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("anthropic")]
    public async Task RetrySettingsReachAllHttpProviders(string api)
    {
        foreach (var (status, retries, attempts, success) in new[] { (503, 2, 3, true), (429, 1, 2, false), (401, 3, 1, false), (503, 0, 1, false) })
        {
            var result = await RunAsync(api, index => index < 3 ? Failure(status) : Success(api), new() { MaxRetries = retries });
            Assert.Equal(attempts, result.Requests);
            Assert.Equal(success ? StopReason.EndTurn : StopReason.Error, result.Message.StopReason);
            Assert.All(result.Bodies, body => Assert.Equal(result.Bodies[0], body));
            Assert.Equal(attempts, result.ResponseObservations);
        }
    }

    /// <summary>【AI】【连接错误】SDK 风格连接和传输超时允许恢复，业务异常不被自动重放。</summary>
    /// <param name="api">协议别名。</param><param name="kind">异常类型。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("chat", "network")]
    [InlineData("compatible", "network")]
    [InlineData("responses", "network")]
    [InlineData("azure", "network")]
    [InlineData("anthropic", "network")]
    [InlineData("chat", "timeout")]
    [InlineData("compatible", "timeout")]
    [InlineData("responses", "timeout")]
    [InlineData("azure", "timeout")]
    [InlineData("anthropic", "timeout")]
    [InlineData("chat", "application")]
    [InlineData("compatible", "application")]
    [InlineData("responses", "application")]
    [InlineData("azure", "application")]
    [InlineData("anthropic", "application")]
    public async Task OnlyTransportFailuresAreRetried(string api, string kind)
    {
        var result = await RunAsync(api, attempt => attempt > 1 ? Success(api) : throw kind switch
        {
            "network" => new HttpRequestException("network fixture"),
            "timeout" => new TaskCanceledException("transport timeout fixture"),
            _ => new InvalidOperationException("application fixture")
        }, new() { MaxRetries = 1 });
        var retried = kind != "application";
        Assert.Equal(retried ? 2 : 1, result.Requests);
        Assert.Equal(retried ? StopReason.EndTurn : StopReason.Error, result.Message.StopReason);
        Assert.Equal(retried ? 1 : 0, result.ResponseObservations);
    }

    /// <summary>【AI】【重试边界】响应后的流观察器失败不得触发新的生成请求。</summary>
    /// <param name="api">协议别名。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("chat")]
    [InlineData("compatible")]
    [InlineData("responses")]
    [InlineData("azure")]
    [InlineData("anthropic")]
    public async Task StreamObserverFailureIsNeverRetried(string api)
    {
        var result = await RunAsync(api, _ => Success(api), new() { MaxRetries = 3,
            OnProviderStreamEvent = (_, _) => throw new InvalidOperationException("stream observer fixture") });
        Assert.Equal(1, result.Requests);
        Assert.Equal(StopReason.Error, result.Message.StopReason);
        Assert.Equal("stream observer fixture", result.Message.ErrorMessage);
    }

    /// <summary>【AI】【失败夹具】使用零等待重试提示避免真实退避时间影响状态回归。</summary>
    /// <param name="status">HTTP 状态。</param><returns>失败响应。</returns>
    private static HttpResponseMessage Failure(int status)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("retry fixture") };
        response.Headers.TryAddWithoutValidation("retry-after-ms", "0");
        return response;
    }

    /// <summary>【AI】【成功夹具】返回各协议具有结束原因的最小 SSE。</summary>
    /// <param name="api">协议别名。</param><returns>成功响应。</returns>
    private static HttpResponseMessage Success(string api) => OpenAiResponsesProviderTests.SseResponse(api switch
    {
        "chat" or "compatible" => "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
        "responses" or "azure" => "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n",
        _ => "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"fixture\",\"usage\":{\"input_tokens\":1}}}\n\n"
            + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n"
            + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
    });

    /// <summary>【AI】【真实入口】在传输层注入响应，记录每次请求及最终模型终态。</summary>
    /// <param name="api">协议别名。</param><param name="respond">按次数生成响应。</param><param name="options">重试配置。</param>
    /// <returns>请求数、正文、响应观察次数及终态。</returns>
    private static async Task<(int Requests, List<string> Bodies, int ResponseObservations, AssistantMessage Message)> RunAsync(
        string api, Func<int, HttpResponseMessage> respond, StreamOptions options)
    {
        var attempts = 0;
        var observations = 0;
        var bodies = new List<string>();
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return respond(++attempts);
        });
        using var client = new HttpClient(handler);
        IStreamProvider provider = api switch
        {
            "chat" => new OpenAiProvider(client), "compatible" => new OpenAiCompatibleProvider("fixture-compatible", "https://fixture.example/v1", httpClient: client),
            "responses" => new OpenAiResponsesProvider(client), "azure" => new AzureOpenAiResponsesProvider(client), _ => new AnthropicProvider(client)
        };
        var model = new Model { Id = "fixture-model", Name = "fixture", Provider = api, Api = provider.Api, BaseUrl = "https://fixture.example/v1" };
        var message = await provider.Stream(model, new() { Messages = [new UserMessage("hello")] }, options with
        { ApiKey = "synthetic", OnResponse = (_, _) => { observations++; return ValueTask.CompletedTask; } }).ResultAsync;
        return (attempts, bodies, observations, message);
    }
}
