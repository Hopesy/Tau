// 作者：xxx
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth.OAuth.Providers;

namespace Tau.Ai.Tests;

/// <summary>【AI】【ChatGPT OAuth 测试】动态注册参数、回调竞争、授权范围与刷新契约。</summary>
public sealed class OpenAIChatGPTOAuthTests
{
    private const string DeviceId = "A1234567-B123-C123-D123-E12345678901";
    private const string Token = """{"access_token":"access","refresh_token":"refresh","expires_in":3600,"scope":"openid chatgpt.tokens.use.direct resource.invoke","id_token":"id-presence-only"}""";
    private const string Redirect = "http://127.0.0.1:1455/auth/callback";
    private static readonly OAuthLoginOptions Options = new(() => DeviceId);

    /// <summary>【AI】【ChatGPT OAuth 测试】手工回调传回动态 client ID，PKCE 与安装 UUID 对应本次授权。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ManualLogin_UsesIssuedClientIdAndPreservesScopes()
    {
        using var handler = new Script((200, Token)); using var client = new HttpClient(handler); var callbacks = new Callbacks(true);
        var provider = Create(client); var credential = await provider.LoginAsync(callbacks, Options);
        var authorize = new Uri(callbacks.Url!); var query = OAuthLoopbackServer<string>.Query(authorize.Query);
        Assert.Equal("https://auth.openai.com/api/accounts/authorize", authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal("dynamic_agent_client", query["client_id"]);
        Assert.Equal("Pi", query["agent_name_hint"]);
        Assert.Equal("urn:uuid:" + DeviceId.ToLowerInvariant(), query["ext_agent_host_id"]);
        Assert.Equal("https://api.openai.com/v1", query["resource"]);
        Assert.Contains("chatgpt.tokens.use.direct", query["scope"].Split(' '));
        Assert.Equal(43, query["state"].Length);
        Assert.Equal(43, query["nonce"].Length);
        Assert.NotEqual(query["state"], query["nonce"]);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("issued-client", request["client_id"]);
        Assert.Equal("authorization_code", request["grant_type"]);
        Assert.Equal("code", request["code"]);
        Assert.Equal(query["redirect_uri"], request["redirect_uri"]);
        Assert.Equal("https://api.openai.com/v1", request["resource"]);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(request["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(query["code_challenge"], challenge);
        Assert.Equal("issued-client", credential.Metadata["clientId"]);
        Assert.Equal(new[] { "openid", "chatgpt.tokens.use.direct", "resource.invoke" }, credential.Properties["scopes"].EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(2000003420000L, credential.ExpiresUnixTimeMilliseconds);
        Assert.Equal("access", provider.GetApiKey(credential));
        Assert.True(callbacks.PromptToken.IsCancellationRequested);
    }

    /// <summary>【AI】【ChatGPT OAuth 测试】缺少 client ID 或 state 错误不消费浏览器回调，成功后停止手动输入。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task BrowserLogin_RejectsInvalidCallbacksAndCompletes()
    {
        using var handler = new Script((200, Token)); using var client = new HttpClient(handler); var callbacks = new Callbacks(false);
        var login = Create(client).LoginAsync(callbacks, Options);
        await callbacks.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var query = OAuthLoopbackServer<string>.Query(new Uri(callbacks.Url!).Query);
        using var browser = new HttpClient();
        using var missingClient = await browser.GetAsync(query["redirect_uri"] + "?code=code&state=" + query["state"]);
        Assert.Equal(HttpStatusCode.BadRequest, missingClient.StatusCode);
        Assert.Contains("issued client ID", await missingClient.Content.ReadAsStringAsync());
        using var wrongState = await browser.GetAsync(query["redirect_uri"] + "?code=code&state=wrong&client_id=issued-client");
        Assert.Equal(HttpStatusCode.BadRequest, wrongState.StatusCode);
        Assert.Empty(handler.Requests);
        using var valid = await browser.GetAsync(query["redirect_uri"] + "?code=code&state=" + query["state"] + "&client_id=issued-client");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal("access", (await login.WaitAsync(TimeSpan.FromSeconds(5))).Access);
        Assert.True(callbacks.PromptToken.IsCancellationRequested);
    }

    /// <summary>【AI】【ChatGPT OAuth 测试】监听端口占用时明确诊断，不打开授权地址。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task BusyPort_ReportsConflictBeforeAuthorization()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var handler = new Script(); using var client = new HttpClient(handler); var callbacks = new Callbacks(true);
        var provider = new OpenAIChatGPTOAuthProvider(client, OAuthFlowClock.System, port, "127.0.0.1");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.LoginAsync(callbacks, Options));
        Assert.Contains($"Port {port} is in use", error.Message);
        Assert.Null(callbacks.Url);
        Assert.Empty(handler.Requests);
    }

    /// <summary>【AI】【ChatGPT OAuth 测试】手工输入必须同源同路径，并包含正确 state 和动态 client ID。</summary>
    /// <param name="input">输入 URL。</param><param name="message">诊断片段。</param>
    [Theory]
    [InlineData("raw-code", "Paste the full callback URL")]
    [InlineData("http://localhost:1455/auth/callback?code=c&state=s&client_id=id", "must start with")]
    [InlineData("https://127.0.0.1:1455/auth/callback?code=c&state=s&client_id=id", "must start with")]
    [InlineData("http://127.0.0.1:1455/other?code=c&state=s&client_id=id", "must start with")]
    [InlineData("http://127.0.0.1:1455/auth/callback?state=s&client_id=id", "Missing authorization code")]
    [InlineData("http://127.0.0.1:1455/auth/callback?code=c&client_id=id", "Missing OAuth state")]
    [InlineData("http://127.0.0.1:1455/auth/callback?code=c&state=wrong&client_id=id", "OAuth state mismatch")]
    [InlineData("http://127.0.0.1:1455/auth/callback?code=c&state=s", "issued client ID")]
    [InlineData("http://127.0.0.1:1455/auth/callback?error=denied", "ChatGPT authorization failed: denied")]
    public void ManualInput_ValidatesRegistrationCallback(string input, string message) => Assert.Contains(message,
        Assert.Throws<InvalidOperationException>(() => OpenAIChatGPTOAuthProvider.ParseManualInput(input, "s", Redirect)).Message);

    /// <summary>【AI】【ChatGPT OAuth 测试】只接受规范 UUID，不接受空值、花括号或空白包围。</summary><param name="deviceId">设备值。</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData(" A1234567-B123-C123-D123-E12345678901 ")]
    [InlineData("{A1234567-B123-C123-D123-E12345678901}")]
    public void DeviceIdentity_RequiresUuid(string? deviceId) => Assert.Contains("requires a device ID", Assert.Throws<InvalidOperationException>(() => OpenAIChatGPTOAuthProvider.AgentHostId(deviceId)).Message);

    /// <summary>【AI】【ChatGPT OAuth 测试】刷新复用动态 client ID，不要求另一个 ID token。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Refresh_UsesIssuedClientWithoutIdTokenRequirement()
    {
        using var handler = new Script((200, Token.Replace(",\"id_token\":\"id-presence-only\"", ""))); using var client = new HttpClient(handler);
        var credential = await Create(client).RefreshTokenAsync(Old());
        Assert.Equal("issued-client", credential.Metadata["clientId"]);
        Assert.Equal("refresh_token", Assert.Single(handler.Requests)["grant_type"]);
        Assert.Equal("old-refresh", handler.Requests[0]["refresh_token"]);
        Assert.False(handler.Requests[0].ContainsKey("code_verifier"));
    }

    /// <summary>【AI】【ChatGPT OAuth 测试】旧凭据缺少 client ID 时要求重新连接，不发请求。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Refresh_MissingClientIdFailsLocally()
    {
        using var handler = new Script(); using var client = new HttpClient(handler);
        var credentials = Old() with { Metadata = new Dictionary<string, string>() };
        Assert.Contains("reconnect ChatGPT", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).RefreshTokenAsync(credentials))).Message);
        Assert.Empty(handler.Requests);
    }

    /// <summary>【AI】【ChatGPT OAuth 测试】响应字段和直接使用范围必须完整。</summary>
    /// <param name="body">响应。</param><param name="message">诊断。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("[]", "OpenAI OAuth token response must be an object")]
    [InlineData("null", "OpenAI OAuth token response must be an object")]
    [InlineData("{}", "OpenAI OAuth token response has invalid access_token")]
    [InlineData("{\"access_token\":\"a\"}", "OpenAI OAuth token response has invalid refresh_token")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\"}", "OpenAI OAuth token response has invalid scope")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"scope\":\"openid\",\"expires_in\":0}", "OpenAI OAuth token response has invalid expires_in")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"scope\":\"openid\",\"expires_in\":3600}", "OpenAI OAuth grant did not include chatgpt.tokens.use.direct")]
    public async Task TokenContract_IsValidated(string body, string message)
    {
        using var handler = new Script((200, body)); using var client = new HttpClient(handler);
        Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).RefreshTokenAsync(Old()))).Message);
    }

    /// <summary>【AI】【ChatGPT OAuth 测试】首次代码兑换必须返回 ID token。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Login_RequiresIdTokenPresence()
    {
        using var handler = new Script((200, Token.Replace(",\"id_token\":\"id-presence-only\"", ""))); using var client = new HttpClient(handler);
        Assert.Equal("OpenAI OAuth token response did not contain an ID token", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(new Callbacks(true), Options))).Message);
    }

    /// <summary>【AI】【ChatGPT OAuth 测试】HTTP 错误包含状态和服务器正文，不当成凭据。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task TokenHttpFailure_ReportsStatusAndBody()
    {
        using var handler = new Script((403, "denied")); using var client = new HttpClient(handler);
        Assert.Equal("OpenAI OAuth token request failed (403): denied", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).RefreshTokenAsync(Old()))).Message);
    }

    /// <summary>【AI】【ChatGPT OAuth 测试】等待登录时取消会释放端口并关闭手动提示。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Cancellation_ReleasesPortAndPrompt()
    {
        using var handler = new Script(); using var client = new HttpClient(handler); using var source = new CancellationTokenSource();
        var callbacks = new Callbacks(false); var login = Create(client).LoginAsync(callbacks, Options, source.Token);
        await callbacks.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var query = OAuthLoopbackServer<string>.Query(new Uri(callbacks.Url!).Query);
        source.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.True(callbacks.PromptToken.IsCancellationRequested);
        using var listener = new TcpListener(IPAddress.Loopback, new Uri(query["redirect_uri"]).Port); listener.Start();
        Assert.Empty(handler.Requests);
    }

    /// <summary>【AI】【ChatGPT OAuth 测试】使用随机回调端口与固定时间。</summary><param name="client">客户端。</param><returns>认证实现。</returns>
    private static OpenAIChatGPTOAuthProvider Create(HttpClient client) => new(client, new OAuthFlowClock { NowMilliseconds = () => 2000000000000 }, 0, "127.0.0.1");
    /// <summary>【AI】【ChatGPT OAuth 测试】带动态客户端 ID 的过期凭据。</summary><returns>凭据。</returns>
    private static OAuthCredentials Old() => new() { Access = "old-access", Refresh = "old-refresh", ExpiresAt = DateTimeOffset.UnixEpoch, Metadata = new Dictionary<string, string> { ["clientId"] = "issued-client" } };

    /// <summary>【AI】【ChatGPT OAuth 测试】仅拦截令牌端点，不接触真实服务。</summary>
    private sealed class Script(params (int Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> _responses = new(responses);
        public List<Dictionary<string, string>> Requests { get; } = [];
        /// <summary>核验端点和表单并回复脚本响应。</summary><param name="request">请求。</param><param name="token">信号。</param><returns>响应。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("https://auth.openai.com/api/accounts/oauth/token", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
            Requests.Add(OAuthLoopbackServer<string>.Query(await request.Content.ReadAsStringAsync(token)));
            var (status, body) = _responses.Dequeue();
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
        }
    }

    /// <summary>【AI】【ChatGPT OAuth 测试】生成有效手工回调或持续等待浏览器。</summary>
    private sealed class Callbacks(bool manual) : IOAuthLoginCallbacks
    {
        public string? Url { get; private set; }
        public CancellationToken PromptToken { get; private set; }
        public TaskCompletionSource PromptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>保存授权 URL。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) => Url = url;
        /// <summary>读取带独立取消信号的手工回调输入。</summary><param name="message">提示。</param><param name="placeholder">地址。</param><param name="token">取消。</param><returns>输入 URL。</returns>
        public async Task<string> OnManualCodeInputAsync(string message, string? placeholder, CancellationToken token)
        {
            PromptToken = token; PromptStarted.TrySetResult();
            if (!manual) { await Task.Delay(Timeout.Infinite, token); return ""; }
            var query = OAuthLoopbackServer<string>.Query(new Uri(Url!).Query);
            Assert.Equal(query["redirect_uri"], placeholder);
            return placeholder + "?code=code&state=" + query["state"] + "&client_id=%20issued-client%20";
        }
        /// <summary>旧手动输入不应被调用。</summary><returns>未使用。</returns>
        public Task<string>? OnManualCodeInputAsync() => throw new NotSupportedException();
        /// <summary>普通输入不应替代手动回调类型。</summary><param name="message">提示。</param><param name="placeholder">地址。</param><param name="allowEmpty">策略。</param><returns>未使用。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new NotSupportedException();
        /// <summary>忽略展示进度。</summary><param name="message">消息。</param>
        public void OnProgress(string message) { }
    }
}
