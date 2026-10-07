// 作者：xxx
using System.Net;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Google;

namespace Tau.Ai.Tests;

/// <summary>【Google】【请求重试回归】仅在 HTTP 响应阶段重试，保留报文、头部和回调语义。</summary>
public sealed class GoogleRequestRetryTests
{
    /// <summary>【Google】【重试状态】默认次数为零，显式次数适用于规定状态且服务端指令优先。</summary>
    /// <param name="vertex">是否使用 Vertex。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusAndHeaderDetermineRetryEligibility(bool vertex)
    {
        foreach (var (status, hint, retryable) in new (int, string?, bool)[] { (408, null, true), (409, null, true), (429, null, true),
            (500, null, true), (503, null, true), (400, null, false), (401, null, false), (503, "false", false), (400, "true", true) })
        {
            var capture = await RunAsync(vertex, attempt => attempt == 1 ? Failure(status, "retry-after-ms", "0", hint) : Success(), new() { MaxRetries = 1 });
            Assert.Equal(retryable ? 2 : 1, capture.Requests);
            Assert.Equal(retryable ? StopReason.EndTurn : StopReason.Error, capture.Message.StopReason);
        }
        foreach (var retries in new int?[] { null, 0, 2 })
        {
            var capture = await RunAsync(vertex, _ => Failure(503, "retry-after-ms", "0"), new() { MaxRetries = retries });
            Assert.Equal(1 + (retries ?? 0), capture.Requests);
            Assert.Equal(StopReason.Error, capture.Message.StopReason);
        }
    }

    /// <summary>【Google】【请求复用】每次请求保留转换后的正文和头部，报文与头回调仅执行一次。</summary>
    /// <param name="vertex">是否使用 Vertex。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetriesPreservePreparedRequestAndResponseObservations(bool vertex)
    {
        var payloads = 0;
        var headers = 0;
        var statuses = new List<int>();
        var result = await RunAsync(vertex, attempt => attempt == 1 ? Failure(429, "retry-after-ms", "0") : Success(), new()
        {
            MaxRetries = 1,
            OnPayload = (payload, _) =>
            {
                payloads++;
                var body = Assert.IsType<Dictionary<string, object>>(payload);
                body["fixture"] = "prepared";
                return ValueTask.FromResult<object?>(body);
            },
            TransformHeaders = (values, _) =>
            {
                headers++;
                values["x-fixture"] = "prepared";
                values["Content-Type"] = "fixture/content";
                return ValueTask.FromResult<IDictionary<string, string?>?>(values);
            },
            OnResponse = (response, _) => { statuses.Add(response.Status); return ValueTask.CompletedTask; }
        });
        Assert.Equal(1, payloads);
        Assert.Equal(1, headers);
        Assert.Equal(new[] { 429, 200 }, statuses);
        Assert.Equal(result.Bodies[0], result.Bodies[1]);
        Assert.Contains("\"fixture\":\"prepared\"", result.Bodies[0]);
        Assert.All(result.Headers, values =>
        {
            Assert.Equal("prepared", values["x-fixture"]);
            Assert.Equal("fixture/content", values["Content-Type"]);
        });
    }

    /// <summary>【Google】【等待头】接受毫秒、秒数、数值前缀与过去的 HTTP 日期。</summary>
    /// <param name="vertex">是否使用 Vertex。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryDelayHeadersAreParsedWithoutLosingLimits(bool vertex)
    {
        foreach (var (header, value) in new[] { ("retry-after-ms", "0.0 fixture"), ("retry-after", "-1"), ("retry-after", "Thu, 01 Jan 1970 00:00:00 GMT") })
        {
            var result = await RunAsync(vertex, attempt => attempt == 1 ? Failure(429, header, value) : Success(), new() { MaxRetries = 1 });
            Assert.Equal(2, result.Requests);
            Assert.Equal(StopReason.EndTurn, result.Message.StopReason);
        }
        foreach (var limit in new TimeSpan?[] { null, TimeSpan.FromSeconds(1) })
        {
            var result = await RunAsync(vertex, _ => Failure(429, "retry-after-ms", "61000"), new() { MaxRetries = 2, MaxRetryDelay = limit });
            Assert.Equal(1, result.Requests);
            Assert.Equal($"Server requested 61s retry delay (max: {(limit is null ? 60 : 1)}s). retry fixture", result.Message.ErrorMessage);
            Assert.Equal(StopReason.Error, result.Message.StopReason);
        }
    }

    /// <summary>【Google】【等待取消】关闭等待上限后仍可取消长等待，不会继续发送请求。</summary>
    /// <param name="vertex">是否使用 Vertex。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInterruptsRetrySleep(bool vertex)
    {
        using var cancellation = new CancellationTokenSource();
        var result = await RunAsync(vertex, _ => Failure(429, "retry-after-ms", "120000"), new()
        {
            MaxRetries = 2, MaxRetryDelay = TimeSpan.Zero, Signal = cancellation.Token,
            OnResponse = (_, _) => { cancellation.CancelAfter(20); return ValueTask.CompletedTask; }
        });
        Assert.Equal(1, result.Requests);
        Assert.Equal(StopReason.Aborted, result.Message.StopReason);
        Assert.Equal("Request was aborted", result.Message.ErrorMessage);
        Assert.Equal("retry-fixture", result.Message.Model);
    }

    /// <summary>【Google】【流边界】成功响应后的损坏流和观察器异常都不得重新生成模型响应。</summary>
    /// <param name="vertex">是否使用 Vertex。</param><param name="observer">是否由响应观察器抛错。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StreamAndObserverFailuresAreNotRetried(bool vertex, bool observer)
    {
        var options = new StreamOptions { MaxRetries = 3 };
        if (observer) options = options with { OnResponse = (_, _) => throw new InvalidOperationException("response observer fixture") };
        var result = await RunAsync(vertex, _ => observer ? Failure(503, "retry-after-ms", "0") : OpenAiResponsesProviderTests.SseResponse("data: {bad-json}\n\n"), options);
        Assert.Equal(1, result.Requests);
        Assert.Equal(StopReason.Error, result.Message.StopReason);
        if (observer) Assert.Equal("response observer fixture", result.Message.ErrorMessage);
    }

    /// <summary>【Google】【失败响应】创建携带重试提示的 HTTP 响应。</summary>
    /// <param name="status">状态码。</param><param name="header">等待头名称。</param><param name="value">等待头值。</param>
    /// <param name="hint">可选是否重试指令。</param><returns>HTTP 响应。</returns>
    private static HttpResponseMessage Failure(int status, string header, string value, string? hint = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("retry fixture") };
        response.Headers.TryAddWithoutValidation(header, value);
        if (hint is not null) response.Headers.TryAddWithoutValidation("x-should-retry", hint);
        return response;
    }

    /// <summary>【Google】【成功响应】创建具有明确结束原因的最小 SSE 响应。</summary><returns>HTTP 响应。</returns>
    private static HttpResponseMessage Success() => OpenAiResponsesProviderTests.SseResponse("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n");

    /// <summary>【Google】【请求夹具】在真实提供方下记录每次 HTTP 请求及最终助手状态。</summary>
    /// <param name="vertex">是否使用 Vertex。</param><param name="respond">按尝试次数返回响应。</param><param name="options">请求配置。</param>
    /// <returns>请求计数、正文、头部及终态。</returns>
    private static async Task<(int Requests, List<string> Bodies, List<Dictionary<string, string>> Headers, AssistantMessage Message)> RunAsync(
        bool vertex, Func<int, HttpResponseMessage> respond, StreamOptions options)
    {
        var count = 0;
        var bodies = new List<string>();
        var headers = new List<Dictionary<string, string>>();
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            headers.Add(request.Headers.Concat(request.Content.Headers).ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase));
            return respond(++count);
        });
        using var client = new HttpClient(handler);
        IStreamProvider provider = vertex ? new GoogleVertexProvider(client) : new GoogleProvider(client);
        var model = new Model { Id = "retry-fixture", Name = "fixture", Provider = "google", Api = provider.Api, BaseUrl = "https://google.example/v1beta" };
        var message = await provider.Stream(model, new() { Messages = [new UserMessage("hi")] }, options with { ApiKey = "synthetic" }).ResultAsync;
        return (count, bodies, headers, message);
    }
}
