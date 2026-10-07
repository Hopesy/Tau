// 作者：xxx
using System.Net;
using Tau.Ai.Providers.Mistral;

namespace Tau.Ai.Tests;

/// <summary>【Mistral】【传输回归】检查实际地址、请求头及传输失败快照。</summary>
public sealed class MistralTransportTests
{
    /// <summary>【Mistral】【地址矩阵】根地址、代理前缀和已有版本地址都生成可调用路径。</summary>
    /// <param name="baseUrl">配置地址。</param><param name="expected">预期绝对地址。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(null, "https://api.mistral.ai/v1/chat/completions")]
    [InlineData("https://api.mistral.ai", "https://api.mistral.ai/v1/chat/completions")]
    [InlineData("https://api.mistral.ai///", "https://api.mistral.ai/v1/chat/completions")]
    [InlineData("https://api.mistral.ai/v1", "https://api.mistral.ai/v1/chat/completions")]
    [InlineData("https://proxy.example/mistral", "https://proxy.example/mistral/v1/chat/completions")]
    [InlineData("https://proxy.example/mistral/v1/", "https://proxy.example/mistral/v1/chat/completions")]
    [InlineData("https://proxy.example/mistral?ignored=true#fragment", "https://proxy.example/mistral/v1/chat/completions")]
    public async Task RequestUsesVersionedMistralEndpoint(string? baseUrl, string expected)
    {
        using var handler = Handler();
        using var client = new HttpClient(handler);
        await new MistralProvider(client).Stream(Model(baseUrl), Context(), Options()).ResultAsync;
        Assert.Equal(expected, handler.RequestUri!.AbsoluteUri);
    }

    /// <summary>【Mistral】【头部覆盖】默认 Accept、授权和内容类型都允许模型与请求覆盖及删除。</summary>
    /// <param name="delete">是否由请求删除模型头部。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestHeadersOverrideDefaultsIncludingContentHeaders(bool delete)
    {
        Dictionary<string, string>? captured = null;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            captured = request.Headers.Concat(request.Content!.Headers).ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("fixture") };
        });
        using var client = new HttpClient(handler);
        var model = Model() with { Headers = new Dictionary<string, string> { ["Accept"] = "model/type", ["Content-Type"] = "model/content", ["Authorization"] = "Model fixture" } };
        var options = Options() with { Headers = new Dictionary<string, string>
        { ["aCCEPT"] = delete ? null! : "request/type", ["content-TYPE"] = delete ? null! : "request/content", ["authorization"] = delete ? null! : "Request fixture" } };
        await new MistralProvider(client).Stream(model, Context(), options).ResultAsync;
        Assert.NotNull(captured);
        foreach (var key in new[] { "Accept", "Content-Type", "Authorization" }) Assert.Equal(!delete, captured!.ContainsKey(key));
        if (!delete)
        {
            Assert.Equal("request/type", captured!["Accept"]);
            Assert.Equal("request/content", captured["Content-Type"]);
            Assert.Equal("Request fixture", captured["Authorization"]);
        }
    }

    /// <summary>【Mistral】【HTTP 错误】格式化长正文、空正文和状态说明，并保留请求身份及响应观察器。</summary>
    /// <param name="kind">正文类型。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("text")]
    [InlineData("long")]
    [InlineData("empty")]
    [InlineData("status")]
    public async Task HttpErrorsAreBoundedAndKeepIdentity(string kind)
    {
        var body = kind switch { "text" => "  invalid request \n", "long" => "  " + new string('a', 4012) + " \n", _ => " \n" };
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest)
        { Content = new StringContent(body), ReasonPhrase = kind == "status" ? "" : "Fixture reason" });
        using var client = new HttpClient(handler);
        var observed = false;
        var message = await new MistralProvider(client).Stream(Model(), Context(), Options() with { OnResponse = (response, _) =>
        {
            Assert.Equal(400, response.Status);
            observed = true;
            return ValueTask.CompletedTask;
        } }).ResultAsync;
        Assert.True(observed);
        AssertIdentity(message);
        var text = kind switch { "text" => "invalid request", "long" => new string('a', 4000) + "... [truncated 12 chars]", "status" => "Request failed with status 400", _ => "Fixture reason" };
        Assert.Equal("Mistral API error (400): " + text, message.ErrorMessage);
    }

    /// <summary>【Mistral】【准备失败】缺失密钥、错误地址或报文回调异常不发送 HTTP，仍返回完整错误快照。</summary>
    /// <param name="kind">失败类型。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("key")]
    [InlineData("url")]
    [InlineData("payload")]
    public async Task PreparationErrorsKeepIdentity(string kind)
    {
        using var handler = Handler();
        using var client = new HttpClient(handler);
        var options = Options();
        if (kind == "key") options = options with { ApiKey = null };
        if (kind == "payload") options = options with { OnPayload = (_, _) => throw new InvalidOperationException("payload fixture") };
        var model = kind == "url" ? Model("https://[invalid") : Model();
        var message = await new MistralProvider(client).Stream(model, Context(), options).ResultAsync;
        Assert.Empty(handler.Requests);
        AssertIdentity(message);
        if (kind == "key") Assert.Equal("No API key for provider: mistral", message.ErrorMessage);
        if (kind == "payload") Assert.Equal("payload fixture", message.ErrorMessage);
    }

    /// <summary>【Mistral】【超时取消】显式请求超时和用户取消具有不同终态，均保留模型及零用量。</summary>
    /// <param name="cancel">是否由用户取消。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportTimeoutAndCancellationRemainDistinct(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new WaitingHandler(cancellation, cancel);
        using var client = new HttpClient(handler);
        var message = await new MistralProvider(client).Stream(Model(), Context(), Options() with
        { Timeout = cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(40), Signal = cancellation.Token }).ResultAsync;
        Assert.Equal(cancel ? StopReason.Aborted : StopReason.Error, message.StopReason);
        Assert.Equal(Model().Id, message.Model);
        Assert.Equal(cancel ? "Request was aborted" : "Request timed out after 40ms", message.ErrorMessage);
        Assert.Equal(0, message.Usage!.Value.TotalTokens);
    }

    /// <summary>【Mistral】【身份断言】验证所有失败均具有来源、零用量及非成功终态。</summary>
    /// <param name="message">返回助手。</param>
    private static void AssertIdentity(AssistantMessage message)
    {
        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Equal("mistral", message.Provider);
        Assert.Equal("mistral-conversations", message.Api);
        Assert.Equal(Model().Id, message.Model);
        Assert.Equal(0, message.Usage!.Value.TotalTokens);
        Assert.False(message.EndTurn);
    }

    /// <summary>【Mistral】【测试模型】创建合成模型。</summary>
    /// <param name="baseUrl">可选地址。</param><returns>模型。</returns>
    private static Model Model(string? baseUrl = "https://api.mistral.ai") => new() { Id = "mistral-fixture", Name = "fixture", Api = "mistral-conversations", Provider = "mistral", BaseUrl = baseUrl };

    /// <summary>【Mistral】【测试上下文】创建最小有效会话。</summary><returns>会话。</returns>
    private static LlmContext Context() => new() { Messages = [new UserMessage("hi")] };

    /// <summary>【Mistral】【测试选项】只使用合成密钥。</summary><returns>请求选项。</returns>
    private static StreamOptions Options() => new() { ApiKey = "synthetic" };

    /// <summary>【Mistral】【捕获处理器】捕获请求后立即返回确定性错误。</summary><returns>HTTP 处理器。</returns>
    private static OpenAiResponsesProviderTests.StubHandler Handler() => new(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("fixture") });

    /// <summary>【Mistral】【等待处理器】等待请求取消，不访问外部网络。</summary>
    private sealed class WaitingHandler(CancellationTokenSource cancellation, bool cancel) : HttpMessageHandler
    {
        /// <summary>【Mistral】【传输等待】按用例触发用户取消，或由请求超时解除等待。</summary>
        /// <param name="request">请求。</param><param name="cancellationToken">请求传输令牌。</param><returns>不会成功返回的响应任务。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (cancel) cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unexpected uncanceled request");
        }
    }
}
