// 作者：xxx
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth.OAuth.Providers;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Anthropic OAuth 测试】验证两种登录方式、PKCE、回调竞争与刷新边界。</summary>
public sealed class AnthropicOAuthTests
{
    private const string Token = """{"access_token":"access","refresh_token":"refresh","expires_in":3600.125}""";

    /// <summary>【AI】【登录方式】浏览器手工回调及复制代码使用各自重定向地址，传输同一 PKCE 与 state。</summary>
    /// <param name="method">所选登录方式。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("browser")]
    [InlineData("copy_code")]
    public async Task Login_UsesSelectedMethodAndPkce(string method)
    {
        using var handler = new Script((200, Token)); using var client = new HttpClient(handler);
        var callbacks = new Callbacks(method) { Input = query => "code#" + query["state"] };
        var provider = Create(client); var credential = await provider.LoginAsync(callbacks);
        var query = callbacks.Query; var request = Assert.Single(handler.Requests);
        Assert.Equal("https://claude.ai/oauth/authorize", new Uri(callbacks.Url!).GetLeftPart(UriPartial.Path));
        Assert.Equal("true", query["code"]); Assert.Equal("code", query["response_type"]);
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", query["client_id"]);
        Assert.Equal("org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload", query["scope"]);
        Assert.Equal("Select Anthropic login method:", callbacks.SelectMessage);
        Assert.Equal(new[] { new OAuthSelectOption("browser", "Browser login (default)"), new OAuthSelectOption("copy_code", "Copy code login (headless)") }, callbacks.Options);
        Assert.Equal(query["redirect_uri"], request.Body.GetProperty("redirect_uri").GetString());
        if (method == "copy_code") { Assert.Equal("https://platform.claude.com/oauth/code/callback", query["redirect_uri"]); Assert.Equal("code#state", callbacks.Placeholder); }
        else { Assert.StartsWith("http://localhost:", query["redirect_uri"]); Assert.Equal(query["redirect_uri"], callbacks.Placeholder); Assert.True(callbacks.PromptToken.IsCancellationRequested); }
        Assert.Equal(query["state"], request.Body.GetProperty("state").GetString());
        var verifier = request.Body.GetProperty("code_verifier").GetString()!;
        Assert.Equal(query["state"], verifier);
        Assert.Equal(query["code_challenge"], Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        Assert.Equal("authorization_code", request.Body.GetProperty("grant_type").GetString()); Assert.Equal("code", request.Body.GetProperty("code").GetString());
        Assert.Equal("application/json", request.ContentType); Assert.Equal("application/json", request.Accept);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", request.Url);
        Assert.Equal("access", provider.GetApiKey(credential)); Assert.Equal("refresh", credential.Refresh); Assert.Equal(4300125L, credential.ExpiresUnixTimeMilliseconds);
        Assert.Equal(new[] { "Exchanging authorization code for tokens..." }, callbacks.Progress);
    }

    /// <summary>【AI】【原生浏览器回调】拒绝错误 state 后仍可接收正确回调，兑换前取消手工提示并释放监听端口。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task BrowserCallback_ValidatesStateAndCancelsManualPrompt()
    {
        var callbacks = new Callbacks("browser");
        using var handler = new Script((200, Token)) { BeforeResponse = () => Assert.True(callbacks.PromptToken.IsCancellationRequested) };
        using var client = new HttpClient(handler); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var login = Create(client).LoginAsync(callbacks, deadline.Token);
        await callbacks.PromptStarted.Task.WaitAsync(deadline.Token);
        var query = callbacks.Query; using var browser = new HttpClient();
        using var wrong = await browser.GetAsync(query["redirect_uri"] + "?code=code&state=wrong", deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode); Assert.Empty(handler.Requests);
        using var valid = await browser.GetAsync(query["redirect_uri"] + "?code=code&state=" + query["state"], deadline.Token);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode); Assert.Equal("access", (await login).Access);
        using var listener = new TcpListener(IPAddress.Loopback, new Uri(query["redirect_uri"]).Port); listener.Start();
    }

    /// <summary>【AI】【端口占用】浏览器模式绑定失败仍可粘贴授权码，复制代码模式完全不依赖监听端口。</summary>
    /// <param name="method">方式。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("browser")]
    [InlineData("copy_code")]
    public async Task BusyPort_AllowsManualLogin(string method)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var handler = new Script((200, Token)); using var client = new HttpClient(handler);
        var callbacks = new Callbacks(method) { Input = _ => "code" };
        var result = await Create(client, port).LoginAsync(callbacks);
        Assert.Equal("access", result.Access);
        Assert.Equal(method == "browser" ? $"http://localhost:{port}/callback" : "https://platform.claude.com/oauth/code/callback", callbacks.Query["redirect_uri"]);
    }

    /// <summary>【AI】【手工校验】两种登录方式均拒绝错误 state 和缺少授权码，不发送兑换请求。</summary>
    /// <param name="method">方式。</param><param name="input">输入。</param><param name="message">预期诊断。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("browser", "code#wrong", "OAuth state mismatch")]
    [InlineData("copy_code", "code#wrong", "OAuth state mismatch")]
    [InlineData("browser", " ", "Missing authorization code")]
    [InlineData("copy_code", "https://platform.claude.com/?state=", "Missing authorization code")]
    public async Task ManualLogin_RejectsInvalidAuthorization(string method, string input, string message)
    {
        using var handler = new Script(); using var client = new HttpClient(handler);
        Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(new Callbacks(method) { Input = _ => input }))).Message);
        Assert.Empty(handler.Requests);
    }

    /// <summary>【AI】【输入兼容】遵循上游的 URL、查询串、井号及原始代码优先级。</summary>
    /// <param name="input">输入。</param><param name="code">代码。</param><param name="state">状态。</param>
    [Theory]
    [InlineData(" raw-code ", "raw-code", null)]
    [InlineData("code#state#ignored", "code", "state")]
    [InlineData("code#", "code", "")]
    [InlineData("code=a+b&state=s&code=ignored", "a b", "s")]
    [InlineData("https://host/callback?code=c&state=s#ignored", "c", "s")]
    [InlineData("custom://host/path?code=c&state=s", "c", "s")]
    [InlineData("https://host/#code", null, null)]
    [InlineData("", null, null)]
    public void AuthorizationInput_MatchesSource(string input, string? code, string? state) => Assert.Equal((code, state), AnthropicOAuthProvider.ParseAuthorizationInput(input));

    /// <summary>【AI】【方式校验】未知方式不得打开浏览器或发送 HTTP。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task UnknownMethod_StopsBeforeAuthorization()
    {
        using var handler = new Script(); using var client = new HttpClient(handler); var callbacks = new Callbacks("other");
        Assert.Equal("Unknown Anthropic login method: other", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(callbacks))).Message);
        Assert.Null(callbacks.Url); Assert.Empty(handler.Requests);
    }

    /// <summary>【AI】【交互取消】选择或手工提示不响应取消时，宿主仍可取消登录并清理监听端口。</summary>
    /// <param name="stage">取消阶段。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("select")]
    [InlineData("browser")]
    [InlineData("copy_code")]
    public async Task Login_CancelsPendingInteraction(string stage)
    {
        using var source = new CancellationTokenSource(); using var handler = new Script(); using var client = new HttpClient(handler);
        var callbacks = new Callbacks(stage == "select" ? "browser" : stage) { BlockSelection = stage == "select" };
        var login = Create(client).LoginAsync(callbacks, source.Token);
        await (stage == "select" ? callbacks.SelectStarted.Task : callbacks.PromptStarted.Task).WaitAsync(TimeSpan.FromSeconds(3));
        source.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(handler.Requests);
        if (stage == "browser") { using var listener = new TcpListener(IPAddress.Loopback, new Uri(callbacks.Query["redirect_uri"]).Port); listener.Start(); }
    }

    /// <summary>【AI】【提示失败】浏览器模式手工提示错误立即结束等待并保留原异常。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ManualFailure_EndsCallbackWait()
    {
        using var handler = new Script(); using var client = new HttpClient(handler);
        var expected = new InvalidOperationException("manual prompt failed");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(new Callbacks("browser") { Input = _ => throw expected }).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Same(expected, actual); Assert.Empty(handler.Requests);
    }

    /// <summary>【AI】【刷新请求】刷新不启动交互，轮转令牌并保留小数秒期限。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Refresh_RotatesTokens()
    {
        using var handler = new Script((200, Token)); using var client = new HttpClient(handler);
        var result = await Create(client).RefreshTokenAsync(Previous());
        Assert.Equal(4300125L, result.ExpiresUnixTimeMilliseconds); Assert.Equal("refresh", result.Refresh);
        var request = Assert.Single(handler.Requests).Body;
        Assert.Equal("refresh_token", request.GetProperty("grant_type").GetString()); Assert.Equal("previous", request.GetProperty("refresh_token").GetString()); Assert.Equal(3, request.EnumerateObject().Count());
    }

    /// <summary>【AI】【请求诊断】HTTP 与无效 JSON 错误包含明确的操作和端点上下文。</summary>
    /// <param name="refresh">是否刷新。</param><param name="status">HTTP 状态。</param><param name="body">正文。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false, 401, "denied")]
    [InlineData(true, 429, "limited")]
    [InlineData(false, 200, "not-json")]
    [InlineData(true, 200, "not-json")]
    public async Task Request_ReportsOperationErrors(bool refresh, int status, string body)
    {
        using var handler = new Script((status, body)); using var client = new HttpClient(handler); var provider = Create(client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => refresh ? provider.RefreshTokenAsync(Previous()) : provider.LoginAsync(new Callbacks("copy_code") { Input = _ => "code" }));
        Assert.Contains(refresh ? "Anthropic token refresh" : "Token exchange", error.Message);
        Assert.Contains(status == 200 ? "returned invalid JSON" : "request failed", error.Message);
        Assert.Contains("https://platform.claude.com/v1/oauth/token", error.Message); Assert.Contains(body, error.Message);
        if (!refresh && status != 200) Assert.Contains("redirect_uri=https://platform.claude.com/oauth/code/callback", error.Message);
    }

    /// <summary>【AI】【请求时限】无响应 HTTP 不得绕过请求期限或调用方取消。</summary>
    /// <param name="cancel">是否主动取消。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_BoundsUncooperativeHttp(bool cancel)
    {
        using var handler = new BlockingHandler(); using var client = new HttpClient(handler); using var source = new CancellationTokenSource();
        var provider = Create(client, timeout: cancel ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(40));
        var task = provider.RefreshTokenAsync(Previous(), source.Token); await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (cancel) source.Cancel();
        try
        {
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
            else Assert.Contains("Anthropic token refresh request failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)))).Message);
        }
        finally { handler.Release.TrySetResult(); await handler.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    /// <summary>【AI】【协议夹具】创建使用隔离监听端口和固定时间的提供方。</summary>
    /// <param name="client">测试 HTTP。</param><param name="port">监听端口。</param><param name="timeout">请求期限。</param><returns>提供方。</returns>
    private static AnthropicOAuthProvider Create(HttpClient client, int port = 0, TimeSpan? timeout = null) => new(client,
        new OAuthFlowClock { NowMilliseconds = () => 1000000 }, port, "127.0.0.1", timeout ?? TimeSpan.FromSeconds(30));

    /// <summary>【AI】【凭据夹具】返回合成旧令牌。</summary><returns>旧凭据。</returns>
    private static OAuthCredentials Previous() => new() { Access = "old", Refresh = "previous", ExpiresAt = DateTimeOffset.UnixEpoch };

    /// <summary>【AI】【交互夹具】捕获类型化方式选择与手工提示，模拟不合作的输入任务。</summary><param name="method">登录方式。</param>
    private sealed class Callbacks(string method) : IOAuthLoginCallbacks
    {
        public string? Url { get; private set; }
        public string? SelectMessage { get; private set; }
        public IReadOnlyList<OAuthSelectOption>? Options { get; private set; }
        public string? Placeholder { get; private set; }
        public CancellationToken PromptToken { get; private set; }
        public Func<Dictionary<string, string>, string>? Input { get; init; }
        public bool BlockSelection { get; init; }
        public List<string> Progress { get; } = [];
        public TaskCompletionSource SelectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PromptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, string> Query => OAuthLoopbackServer<string>.Query(new Uri(Url!).Query);
        /// <inheritdoc />
        public void OnAuth(string url, string? instructions = null) => Url = url;
        /// <inheritdoc />
        public Task<string?> OnSelectAsync(string message, IReadOnlyList<OAuthSelectOption> options, CancellationToken token)
        { SelectMessage = message; Options = options; SelectStarted.TrySetResult(); return BlockSelection ? new TaskCompletionSource<string?>().Task : Task.FromResult<string?>(method); }
        /// <inheritdoc />
        public Task<string> OnManualCodeInputAsync(string message, string? placeholder, CancellationToken token)
        { Placeholder = placeholder; PromptToken = token; PromptStarted.TrySetResult(); return Input is null ? new TaskCompletionSource<string>().Task : Task.FromResult(Input(Query)); }
        /// <inheritdoc />
        public void OnProgress(string message) => Progress.Add(message);
        /// <inheritdoc />
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new InvalidOperationException("Expected typed prompt");
        /// <inheritdoc />
        public Task<string>? OnManualCodeInputAsync() => throw new InvalidOperationException("Expected typed manual prompt");
    }

    /// <summary>【AI】【请求夹具】只保存测试请求。</summary>
    private sealed record Request(string Url, string? Accept, string? ContentType, JsonElement Body);

    /// <summary>【AI】【响应脚本】捕获 JSON 并按次序响应。</summary><param name="responses">响应列表。</param>
    private sealed class Script(params (int Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> _responses = new(responses);
        public List<Request> Requests { get; } = [];
        public Action? BeforeResponse { get; init; }
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Headers.Accept.ToString(), request.Content.Headers.ContentType?.ToString(), body.RootElement.Clone()));
            BeforeResponse?.Invoke(); var response = _responses.Dequeue(); return new((HttpStatusCode)response.Status) { Content = new StringContent(response.Body) };
        }
    }

    /// <summary>【AI】【阻塞 HTTP】忽略取消但允许测试释放，以验证外部有界等待。</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Started.TrySetResult(); await Release.Task; Finished.TrySetResult(); return new(HttpStatusCode.OK) { Content = new StringContent(Token) }; }
    }
}
