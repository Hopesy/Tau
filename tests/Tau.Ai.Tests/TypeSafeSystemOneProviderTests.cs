// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.TypeSafe;

namespace Tau.Ai.Tests;

/// <summary>【AI】【System One 测试】验证真实 HTTP 协议边界，无需外部网络或密钥。</summary>
public sealed class TypeSafeSystemOneProviderTests
{
    private static readonly ClassifierModel Model = new()
    {
        Id = "jev-latest", Name = "Jev", Provider = "typesafe", Api = "typesafe-system-one",
        BaseUrl = "https://unit.test/v1/", Cost = new ModelCost(1m, 2m)
    };
    private const string ValidResponse = """
        {"answers":{"route":{"type":"choice","choice":"yes","probabilities":{"yes":0.9,"no":0.1},"confidence":0.8},
        "quality":{"type":"score","score":4.5,"confidence":0.7},"safe":{"type":"noul","noul":0.95},"extra":{"ignored":true}},
        "usage":{"input_tokens":100,"output_tokens":20}}
        """;

    /// <summary>检查模型、状态、三类问题、请求头覆盖及答案转换和成本。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Classify_MapsWireRequestAndParsesAllAnswers()
    {
        using var handler = new Handler(async (request, _) =>
        {
            Assert.Equal("https://unit.test/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer override", request.Headers.Authorization!.ToString());
            Assert.Equal("request", Assert.Single(request.Headers.GetValues("X-Shared")));
            Assert.Equal("application/custom", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
            Assert.Equal("jev-latest", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("你好", body.RootElement.GetProperty("state").GetProperty("text").GetString());
            var questions = body.RootElement.GetProperty("questions");
            Assert.Equal("choice", questions.GetProperty("route").GetProperty("type").GetString());
            Assert.Equal(2, questions.GetProperty("quality").GetProperty("criteria").GetArrayLength());
            Assert.Equal("noul", questions.GetProperty("safe").GetProperty("type").GetString());
            Assert.Equal("safe", questions.GetProperty("safe").GetProperty("criteria").GetProperty("true").GetString());
            return Response(ValidResponse);
        });
        using var client = new HttpClient(handler);
        var provider = new TypeSafeSystemOneProvider(client);
        var callbackCount = 0;
        var result = await provider.ClassifyAsync(Model with { Headers = new Dictionary<string, string> { ["X-Shared"] = "model" } }, Context(), new ClassifierOptions
        {
            ApiKey = "key",
            Headers = new Dictionary<string, string> { ["authorization"] = "Bearer override", ["x-shared"] = "request", ["Content-Type"] = "application/custom" },
            OnResponse = (response, model) =>
            {
                Assert.Equal(200, response.Status);
                Assert.Equal("response-id", response.Headers["X-Request-Id"]);
                Assert.Equal(Model.Id, model.Id);
                callbackCount++;
                return ValueTask.CompletedTask;
            }
        });
        Assert.True(result.StopReason == ClassifierStopReason.Stop, result.ErrorMessage);
        Assert.Equal(3, result.Answers.Count);
        Assert.Equal("yes", Assert.IsType<ClassifierChoiceAnswer>(result.Answers["route"]).Choice);
        Assert.Equal(4.5, Assert.IsType<ClassifierScoreAnswer>(result.Answers["quality"]).Score);
        Assert.Equal(0.95, Assert.IsType<ClassifierBoolAnswer>(result.Answers["safe"]).Probability);
        Assert.Equal(120, result.Usage!.Value.TotalTokens);
        Assert.Equal(0.00014m, result.Usage.Value.Cost!.Value.Total);
        Assert.Equal(1, callbackCount);
    }

    /// <summary>缺失、错误类型和非法数值不能产生部分成功答案，已计费用量仍保留。</summary>
    /// <param name="answers">损坏的答案对象。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"safe\":{\"type\":\"bool\",\"probability\":0.5}}")]
    [InlineData("{\"safe\":{\"type\":\"noul\",\"noul\":\"bad\"}}")]
    [InlineData("{\"safe\":{\"type\":\"noul\",\"noul\":1e999}}")]
    public async Task Classify_InvalidAnswersPreserveBilledUsage(string answers)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response("{\"answers\":" + answers + ",\"usage\":{\"input_tokens\":100}}")));
        using var client = new HttpClient(handler);
        var context = Context() with { Questions = new Dictionary<string, ClassifierQuestion> { ["safe"] = new ClassifierBoolQuestion("", new("", "")) } };
        var result = await new TypeSafeSystemOneProvider(client).ClassifyAsync(Model, context, new ClassifierOptions { ApiKey = "key" });
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Empty(result.Answers);
        Assert.Equal(100, result.Usage!.Value.InputTokens);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>选择和评分字段必须符合协议类型。</summary>
    /// <param name="field">响应中待破坏的文本。</param>
    /// <param name="replacement">替换文本。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("\"choice\":\"yes\"", "\"choice\":3")]
    [InlineData("\"yes\":0.9", "\"yes\":null")]
    [InlineData("\"confidence\":0.8", "\"confidence\":\"bad\"")]
    [InlineData("\"score\":4.5", "\"score\":null")]
    public async Task Classify_RejectsMalformedChoiceAndScore(string field, string replacement)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(ValidResponse.Replace(field, replacement, StringComparison.Ordinal))));
        using var client = new HttpClient(handler);
        var result = await new TypeSafeSystemOneProvider(client).ClassifyAsync(Model, Context(), new ClassifierOptions { ApiKey = "key" });
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Empty(result.Answers);
        Assert.NotNull(result.Usage);
    }

    /// <summary>用量对象可缺失或部分损坏，不影响有效答案。</summary>
    /// <param name="usage">替换的用量对象。</param>
    /// <param name="expectedInput">预期输入计数，负数代表无用量。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("null", -1)]
    [InlineData("{}", -1)]
    [InlineData("{\"input_tokens\":\"bad\",\"output_tokens\":-3}", 0)]
    [InlineData("{\"input_tokens\":7}", 7)]
    public async Task Classify_ToleratesOptionalUsage(string usage, int expectedInput)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response("{\"answers\":{},\"usage\":" + usage + "}")));
        using var client = new HttpClient(handler);
        var context = Context() with { Questions = new Dictionary<string, ClassifierQuestion>() };
        var result = await new TypeSafeSystemOneProvider(client).ClassifyAsync(Model, context, new ClassifierOptions { ApiKey = "key" });
        Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        if (expectedInput < 0) Assert.Null(result.Usage);
        else Assert.Equal(expectedInput, result.Usage!.Value.InputTokens);
    }

    /// <summary>重试沿用一次转换后的请求体，成功响应回调只调用一次。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Classify_RetriesHttpErrorsAndTransformsPayloadOnce()
    {
        var attempts = 0;
        var payloadCalls = 0;
        var responseCalls = 0;
        using var handler = new Handler(async (request, _) =>
        {
            Assert.Equal("{\"replacement\":true}", await request.Content!.ReadAsStringAsync());
            attempts++;
            return attempts == 1 ? Response("busy", HttpStatusCode.TooManyRequests) : Response(ValidResponse);
        });
        using var client = new HttpClient(handler);
        var result = await new TypeSafeSystemOneProvider(client).ClassifyAsync(Model, Context(), new ClassifierOptions
        {
            ApiKey = "key",
            OnPayload = (payload, _) =>
            {
                Assert.Equal("jev-latest", payload.GetProperty("model").GetString());
                payloadCalls++;
                using var replacement = JsonDocument.Parse("{\"replacement\":true}");
                return ValueTask.FromResult<JsonElement?>(replacement.RootElement.Clone());
            },
            OnResponse = (_, _) => { responseCalls++; return ValueTask.CompletedTask; }
        });
        Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        Assert.Equal(2, attempts);
        Assert.Equal(1, payloadCalls);
        Assert.Equal(1, responseCalls);
    }

    /// <summary>普通客户端错误、显式禁止重试和零重试设置都只发送一次。</summary>
    /// <param name="status">HTTP 状态。</param>
    /// <param name="retryHeader">服务端重试覆盖。</param>
    /// <param name="maxRetries">请求重试上限。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(400, null, 2)]
    [InlineData(503, "false", 2)]
    [InlineData(503, null, 0)]
    public async Task Classify_RespectsRetryPolicy(int status, string? retryHeader, int maxRetries)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Response("failure", (HttpStatusCode)status);
            if (retryHeader is not null) response.Headers.TryAddWithoutValidation("x-should-retry", retryHeader);
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var result = await new TypeSafeSystemOneProvider(client).ClassifyAsync(Model, Context(), new ClassifierOptions { ApiKey = "key", MaxRetries = maxRetries });
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Contains(status.ToString(), result.ErrorMessage!);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>服务端等待时间超过调用方上限时立即结束，不再次发请求。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Classify_RejectsExcessiveRetryDelay()
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Response("busy", HttpStatusCode.TooManyRequests);
            response.Headers.Remove("retry-after-ms");
            response.Headers.TryAddWithoutValidation("retry-after-ms", "5000");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var result = await new TypeSafeSystemOneProvider(client).ClassifyAsync(Model, Context(), new ClassifierOptions { ApiKey = "key", MaxRetryDelay = TimeSpan.FromSeconds(1) });
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Contains("retry delay", result.ErrorMessage!);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>区分请求超时和调用方取消，均返回结果而不抛出异常。</summary>
    /// <param name="cancel">是否由调用方取消。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Classify_DistinguishesTimeoutFromCancellation(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        using var handler = new Handler(async (_, token) =>
        {
            if (cancel) cts.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(ValidResponse);
        });
        using var client = new HttpClient(handler);
        var result = await new TypeSafeSystemOneProvider(client).ClassifyAsync(Model, Context(), new ClassifierOptions
        {
            ApiKey = "key", Signal = cts.Token, Timeout = TimeSpan.FromMilliseconds(30), MaxRetries = 0
        });
        Assert.Equal(cancel ? ClassifierStopReason.Aborted : ClassifierStopReason.Error, result.StopReason);
        if (!cancel) Assert.Contains("timed out", result.ErrorMessage!);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>参数校验和请求体回调失败不会发送 HTTP。</summary>
    /// <param name="failure">失败入口。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("api")]
    [InlineData("key")]
    [InlineData("callback")]
    [InlineData("cancel")]
    public async Task Classify_StopsBeforeHttpOnPreparationFailure(string failure)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        if (failure == "cancel") cts.Cancel();
        var result = await new TypeSafeSystemOneProvider(client).ClassifyAsync(failure == "api" ? Model with { Api = "wrong" } : Model, Context(), new ClassifierOptions
        {
            ApiKey = failure == "key" ? null : "key", Signal = cts.Token,
            OnPayload = failure == "callback" ? (_, _) => throw new InvalidOperationException("callback failed") : null
        });
        Assert.Equal(failure == "cancel" ? ClassifierStopReason.Aborted : ClassifierStopReason.Error, result.StopReason);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>通过统一入口解析环境 key 并命中内置 TypeSafe 地址。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task BuiltInModels_ClassifiesWithEnvironmentAuthentication()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://api.typesafe.ai/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer env-key", request.Headers.Authorization!.ToString());
            return Task.FromResult(Response(ValidResponse));
        });
        using var client = new HttpClient(handler);
        var models = BuiltInProviders.CreateBuiltInModels(httpClient: client);
        var model = models.GetModelOfType(ModelTypes.Classifier, "typesafe", "jev-latest")!;
        var result = await models.ClassifyAsync(model, Context(), new ClassifierOptions { Env = new Dictionary<string, string> { ["TYPESAFE_API_KEY"] = "env-key" } });
        Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        Assert.Equal(3, result.Answers.Count);
    }

    /// <summary>无 HTTP 状态的连接失败可重试，默认最多执行三次。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Classify_RetriesConnectionFailuresWithinDefaultBudget()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("connection reset"));
        using var client = new HttpClient(handler);
        var result = await new TypeSafeSystemOneProvider(client).ClassifyAsync(Model, Context(), new ClassifierOptions { ApiKey = "key" });
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Contains("connection reset", result.ErrorMessage!);
        Assert.Equal(3, handler.Calls);
    }

    /// <summary>解析失败及响应回调异常不能触发重复请求。</summary>
    /// <param name="callbackFails">是否模拟响应回调异常。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Classify_DoesNotRetryResponseProcessingErrors(bool callbackFails)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(callbackFails ? ValidResponse : "not-json")));
        using var client = new HttpClient(handler);
        var result = await new TypeSafeSystemOneProvider(client).ClassifyAsync(Model, Context(), new ClassifierOptions
        {
            ApiKey = "key",
            OnResponse = callbackFails ? (_, _) => throw new InvalidOperationException("response callback failed") : null
        });
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>每次尝试使用新的超时预算，首次超时后能够成功重试。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Classify_RenewsTimeoutForEachAttempt()
    {
        var attempts = 0;
        using var handler = new Handler(async (_, token) =>
        {
            if (++attempts == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(ValidResponse);
        });
        using var client = new HttpClient(handler);
        var result = await new TypeSafeSystemOneProvider(client).ClassifyAsync(Model, Context(), new ClassifierOptions
        {
            ApiKey = "key", Timeout = TimeSpan.FromMilliseconds(100), MaxRetries = 1
        });
        Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>构造三类问题和包含中文的对象状态。</summary>
    /// <returns>独立 JSON 上下文。</returns>
    private static ClassifierContext Context()
    {
        using var state = JsonDocument.Parse("{\"text\":\"你好\"}");
        return new ClassifierContext(state.RootElement.Clone(), new Dictionary<string, ClassifierQuestion>
        {
            ["route"] = new ClassifierChoiceQuestion("choose", new Dictionary<string, string> { ["yes"] = "accept", ["no"] = "reject" }),
            ["quality"] = new ClassifierScoreQuestion("rate", ["bad", "good"]),
            ["safe"] = new ClassifierBoolQuestion("check", new("safe", "unsafe"))
        });
    }

    /// <summary>创建带可控重试头的模拟响应。</summary>
    /// <param name="body">响应正文。</param>
    /// <param name="status">HTTP 状态。</param>
    /// <returns>响应消息。</returns>
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        response.Headers.TryAddWithoutValidation("retry-after-ms", "0");
        response.Headers.TryAddWithoutValidation("X-Request-Id", "response-id");
        return response;
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public int Calls { get; private set; }
        /// <summary>创建可注入响应行为的传输。</summary>
        /// <param name="send">请求处理委托。</param>
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return _send(request, cancellationToken);
        }
    }
}
