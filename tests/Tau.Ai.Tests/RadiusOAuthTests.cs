// 作者：xxx
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth.OAuth.Providers;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Radius OAuth 测试】验证自定义网关的浏览器与设备码登录。</summary>
public sealed class RadiusOAuthTests
{
    private const string Device = """{"device_code":"device","user_code":"USER","verification_uri":"https://gateway.test/verify","interval":2,"expires_in":120}""";
    private const string Token = """{"access_token":"access","refresh_token":"refresh","expires_in":3600,"scope":"gateway offline_access"}""";

    /// <summary>【AI】【Radius 浏览器测试】授权入口可以在其他主机，兑换仍发送到网关，state 错误不消费授权。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task BrowserLogin_UsesDiscoveryStateAndPkce()
    {
        using var handler = new Script((200, """{"authorizationEndpoint":"https://accounts.test/authorize?old=removed"}"""), (200, Token));
        using var client = new HttpClient(handler); var callbacks = new Callbacks("browser"); var clock = new Time();
        var login = Create(client, clock).LoginAsync(callbacks);
        var url = await callbacks.Authorization.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var query = OAuthLoopbackServer<string>.Query(new Uri(url).Query);
        Assert.Equal("accounts.test", new Uri(url).Host);
        Assert.False(query.ContainsKey("old"));
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("pi-gateway", query["client_id"]);
        Assert.Equal("gateway offline_access", query["scope"]);
        Assert.Equal("url", query["handoff"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.True(Guid.TryParse(query["state"], out _));
        using var browser = new HttpClient();
        using var wrong = await browser.GetAsync(query["redirect_uri"] + "?code=bad&state=wrong");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Single(handler.Requests);
        using var response = await browser.GetAsync(query["redirect_uri"] + "?code=browser&state=" + query["state"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var credentials = await login.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("access", credentials.Access);
        Assert.Equal("gateway offline_access", credentials.Metadata["scope"]);
        Assert.Equal(clock.Now + 3540000, credentials.ExpiresUnixTimeMilliseconds);
        Assert.Equal("https://gateway.test/v1/oauth", handler.Requests[0].Url);
        Assert.Equal("GET", handler.Requests[0].Method);
        var exchange = handler.Requests[1];
        Assert.Equal("https://gateway.test/v1/oauth/token", exchange.Url);
        Assert.Equal("authorization_code", exchange.Fields["grant_type"]);
        Assert.Equal("browser", exchange.Fields["code"]);
        Assert.Equal(query["redirect_uri"], exchange.Fields["redirect_uri"]);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(exchange.Fields["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(query["code_challenge"], challenge);
    }

    /// <summary>【AI】【Radius 浏览器测试】兑换失败同时反映在浏览器和登录结果。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task BrowserExchangeFailure_ReturnsFailurePage()
    {
        using var handler = new Script((200, """{"authorizationEndpoint":"https://accounts.test/authorize"}"""), (400, """{"error":"invalid_grant","error_description":"expired"}"""));
        using var client = new HttpClient(handler); var callbacks = new Callbacks("browser");
        var login = Create(client).LoginAsync(callbacks);
        var query = OAuthLoopbackServer<string>.Query(new Uri(await callbacks.Authorization.Task.WaitAsync(TimeSpan.FromSeconds(5))).Query);
        using var browser = new HttpClient();
        using var response = await browser.GetAsync(query["redirect_uri"] + "?code=browser&state=" + query["state"]);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("invalid_grant: expired", await response.Content.ReadAsStringAsync());
        Assert.Equal("Radius OAuth token request failed: invalid_grant: expired", (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => login)).Message);
    }

    /// <summary>【AI】【Radius 设备测试】不发现浏览器地址，立即轮询，限速增加五秒。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task DeviceLogin_PollsImmediatelyAndPreservesScope()
    {
        using var handler = new Script((200, Device), (400, """{"error":"authorization_pending"}"""), (400, """{"error":"slow_down","interval":30}"""), (200, Token));
        using var client = new HttpClient(handler); var time = new Time(); var callbacks = new Callbacks("device-code");
        var credential = await Create(client, time).LoginAsync(callbacks);
        Assert.Equal(new double[] { 2000, 7000 }, time.Delays);
        Assert.Equal("https://gateway.test/v1/oauth/device", handler.Requests[0].Url);
        Assert.Equal("gateway offline_access", handler.Requests[0].Fields["scope"]);
        Assert.All(handler.Requests.Skip(1), request => Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", request.Fields["grant_type"]));
        Assert.Equal("access", credential.Access);
        Assert.Equal("gateway offline_access", credential.Metadata["scope"]);
        Assert.Equal("USER", callbacks.DeviceCode);
        Assert.False(callbacks.Authorization.Task.IsCompleted);
    }

    /// <summary>【AI】【Radius 设备测试】授权拒绝、过期和未知错误正确终止。</summary>
    /// <param name="error">协议代码。</param><param name="message">诊断。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("access_denied", "Device authorization was denied.")]
    [InlineData("expired_token", "Device authorization expired.")]
    [InlineData("invalid_client", "Radius OAuth token request failed: invalid_client")]
    public async Task DeviceFailure_IsTerminal(string error, string message)
    {
        using var handler = new Script((200, Device), (400, "{\"error\":\"" + error + "\"}")); using var client = new HttpClient(handler);
        Assert.Equal(message, (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Create(client).LoginAsync(new Callbacks("device-code")))).Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>【AI】【Radius 刷新测试】使用同一网关和客户端标识，保留一分钟到期余量。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Refresh_UsesGatewayAndSkew()
    {
        using var handler = new Script((200, Token)); using var client = new HttpClient(handler); var time = new Time();
        var credential = await Create(client, time).RefreshTokenAsync(Old());
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://gateway.test/v1/oauth/token", request.Url);
        Assert.Equal("refresh_token", request.Fields["grant_type"]);
        Assert.Equal("old-refresh", request.Fields["refresh_token"]);
        Assert.Equal("pi-gateway", request.Fields["client_id"]);
        Assert.Equal(time.Now + 3540000, credential.ExpiresUnixTimeMilliseconds);
    }

    /// <summary>【AI】【Radius 错误测试】错误正文可为 OAuth JSON、文本或空内容。</summary>
    /// <param name="body">正文。</param><param name="detail">诊断详情。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("{\"error\":\"invalid_grant\",\"error_description\":\"revoked\"}", "invalid_grant: revoked")]
    [InlineData("{\"error_description\":\"unavailable\"}", "unavailable")]
    [InlineData("plain error", "plain error")]
    [InlineData("{}", "400")]
    [InlineData("", "400")]
    public async Task RefreshError_PreservesDetail(string body, string detail)
    {
        using var handler = new Script((400, body)); using var client = new HttpClient(handler);
        Assert.Equal("Radius OAuth token request failed: " + detail, (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Create(client).RefreshTokenAsync(Old()))).Message);
    }

    /// <summary>【AI】【Radius 发现测试】配置缺失或错误状态会在打开浏览器前失败。</summary>
    /// <param name="status">状态。</param><param name="body">正文。</param><param name="message">诊断。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(503, "busy", "Could not load Radius OAuth config from https://gateway.test/subpath: 503 busy")]
    [InlineData(200, "{}", "Invalid Radius OAuth config from https://gateway.test/subpath")]
    public async Task DiscoveryError_StopsBeforeBrowser(int status, string body, string message)
    {
        using var handler = new Script((status, body)); using var client = new HttpClient(handler); var callbacks = new Callbacks("browser");
        Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(callbacks))).Message);
        Assert.False(callbacks.Authorization.Task.IsCompleted);
    }

    /// <summary>【AI】【Radius 取消测试】等待浏览器时取消会关闭回调端口。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task BrowserCancellation_ReleasesListener()
    {
        using var handler = new Script((200, """{"authorizationEndpoint":"https://accounts.test/authorize"}""")); using var client = new HttpClient(handler);
        using var source = new CancellationTokenSource(); var callbacks = new Callbacks("browser");
        var login = Create(client).LoginAsync(callbacks, source.Token);
        var query = OAuthLoopbackServer<string>.Query(new Uri(await callbacks.Authorization.Task.WaitAsync(TimeSpan.FromSeconds(5))).Query);
        source.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, new Uri(query["redirect_uri"]).Port);
        listener.Start();
    }

    /// <summary>【AI】【Radius 选择测试】未知方式及取消不产生网络请求。</summary>
    /// <param name="method">选择结果。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    public async Task InvalidSelection_DoesNotRequest(string? method)
    {
        using var handler = new Script(); using var client = new HttpClient(handler);
        if (method is null) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(client).LoginAsync(new Callbacks(method)));
        else Assert.Equal("Unknown My Gateway sign-in method: unknown", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(new Callbacks(method)))).Message);
        Assert.Empty(handler.Requests);
    }

    /// <summary>【AI】【Radius 目录测试】内置 Radius 暴露账户和密钥两种方式，不标记成订阅。</summary>
    [Fact]
    public void BuiltInRadius_ExposesBothAuthenticationMethods()
    {
        var config = new ModelConfigurationStore([]);
        var resolver = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: config);
        Assert.IsType<RadiusOAuthProvider>(resolver.GetOAuthProvider("radius"));
        var auth = BuiltInProviders.CreateBuiltInModels(config, resolver).GetProvider("radius")!.Auth!;
        Assert.Equal("Radius", auth.OAuth!.Name);
        Assert.False(auth.OAuth.IsSubscription);
        Assert.NotNull(auth.ApiKey);
    }

    /// <summary>【AI】【Radius 测试】隔离回调端口并注入虚拟时间。</summary><param name="client">客户端。</param><param name="time">时钟。</param><returns>认证。</returns>
    private static RadiusOAuthProvider Create(HttpClient client, Time? time = null) => new(" gateway.test/subpath/ ", "My Gateway", "custom-radius", client, (time ?? new Time()).Clock, 0);
    /// <summary>【AI】【Radius 测试】生成旧凭据。</summary><returns>凭据。</returns>
    private static OAuthCredentials Old() => new() { Access = "old", Refresh = "old-refresh", ExpiresAt = DateTimeOffset.UnixEpoch };

    /// <summary>【AI】【Radius 测试】请求协议字段。</summary>
    private sealed record Request(string Url, string Method, Dictionary<string, string> Fields);
    /// <summary>【AI】【Radius 测试】按脚本回复网关请求。</summary>
    private sealed class Script(params (int Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> _responses = new(responses);
        public List<Request> Requests { get; } = [];
        /// <summary>记录地址和表单。</summary><param name="request">请求。</param><param name="token">信号。</param><returns>响应。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Contains(request.Headers.Accept, item => item.MediaType == "application/json");
            if (request.Content is not null) Assert.Equal("application/x-www-form-urlencoded", request.Content.Headers.ContentType!.MediaType);
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Method.Method, OAuthLoopbackServer<string>.Query(request.Content is null ? "" : await request.Content.ReadAsStringAsync(token))));
            var (status, body) = _responses.Dequeue();
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
        }
    }
    /// <summary>【AI】【Radius 测试】虚拟时钟。</summary>
    private sealed class Time
    {
        public long Now { get; private set; } = 2000000000000L;
        public List<double> Delays { get; } = [];
        public OAuthFlowClock Clock { get; }
        /// <summary>绑定虚拟时间。</summary>
        public Time() => Clock = new OAuthFlowClock { NowMilliseconds = () => Now, SleepAsync = SleepAsync };
        /// <summary>立即推进等待。</summary><param name="milliseconds">毫秒。</param><param name="token">信号。</param><returns>完成任务。</returns>
        private Task SleepAsync(double milliseconds, CancellationToken token) { token.ThrowIfCancellationRequested(); Now += (long)milliseconds; Delays.Add(milliseconds); return Task.CompletedTask; }
    }
    /// <summary>【AI】【Radius 测试】选择登录方式并接收授权地址。</summary>
    private sealed class Callbacks(string? method) : IOAuthLoginCallbacks
    {
        public TaskCompletionSource<string> Authorization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? DeviceCode { get; private set; }
        /// <summary>保留浏览器地址。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) => Authorization.TrySetResult(url);
        /// <summary>核验两个候选项。</summary><param name="message">提示。</param><param name="options">候选项。</param><returns>预选方式。</returns>
        public Task<string?> OnSelectAsync(string message, IReadOnlyList<OAuthSelectOption> options)
        { Assert.Equal("Sign in to My Gateway:", message); Assert.Equal(new[] { "browser", "device-code" }, options.Select(option => option.Id)); return Task.FromResult(method); }
        /// <summary>记录设备码。</summary><param name="userCode">代码。</param><param name="verificationUri">地址。</param><param name="intervalSeconds">间隔。</param><param name="expiresInSeconds">期限。</param>
        public void OnDeviceCode(string userCode, string verificationUri, int? intervalSeconds = null, int? expiresInSeconds = null) => DeviceCode = userCode;
        /// <summary>忽略展示性进度。</summary><param name="message">消息。</param>
        public void OnProgress(string message) { }
        /// <summary>本流程不读取手工代码。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">策略。</param><returns>未使用。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new NotSupportedException();
        /// <summary>没有后台手工代码输入。</summary><returns>空。</returns>
        public Task<string>? OnManualCodeInputAsync() => null;
    }
}
