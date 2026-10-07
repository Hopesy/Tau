// 作者：xxx
using System.Net;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth.OAuth.Providers;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Meta OAuth 测试】核验身份令牌与模型密钥分离、轮询状态及续期错误。</summary>
public sealed class MetaOAuthTests
{
    private const string Device = """{"device_code":"device","user_code":"USER","verification_uri":"https://auth.meta.com/device","verification_uri_complete":"https://auth.meta.com/device?code=USER","interval":2,"expires_in":120}""";

    /// <summary>【AI】【Meta OAuth 测试】申请、等待、减速、授权与密钥铸造按协议顺序执行。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Login_ExchangesIdentityAndRefreshRemintsKey()
    {
        using var handler = new ScriptedHandler((200, Device), (400, """{"error":"authorization_pending"}"""),
            (400, """{"error":"slow_down","interval":3}"""), (200, """{"access_token":"identity-token"}"""),
            (200, """{"api_key":"model-key"}"""), (200, """{"api_key":"renewed-key"}"""));
        using var client = new HttpClient(handler); var time = new TestClock(); var callbacks = new DeviceCallbacks();
        var provider = new MetaOAuthProvider(client, time.Clock, TimeSpan.FromSeconds(5));
        var credentials = await provider.LoginAsync(callbacks);
        Assert.Equal(new double[] { 2000, 2000, 3000 }, time.Waits);
        Assert.Equal("identity-token", credentials.Refresh);
        Assert.Equal("model-key", credentials.Access);
        Assert.Equal("model-key", provider.GetApiKey(credentials));
        Assert.Equal(time.Now + 86400000, credentials.ExpiresUnixTimeMilliseconds);
        Assert.Equal(("USER", "https://auth.meta.com/device?code=USER", 2, 120), Assert.Single(callbacks.Devices));
        Assert.Contains("Enabling Meta Model API access...", callbacks.Progress);
        var renewed = await provider.RefreshTokenAsync(credentials);
        Assert.Equal("renewed-key", renewed.Access);
        Assert.Equal("identity-token", renewed.Refresh);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal("https://auth.meta.com/oidc/device/authorization/", handler.Requests[0].Url);
        Assert.Equal("client_id=1031625952748946", handler.Requests[0].Body);
        Assert.All(handler.Requests.Skip(1).Take(3), request =>
        {
            Assert.Equal("https://auth.meta.com/oidc/device/token/", request.Url);
            var fields = OAuthLoopbackServer<string>.Query(request.Body);
            Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", fields["grant_type"]);
            Assert.Equal("device", fields["device_code"]);
            Assert.Null(request.Authorization);
        });
        Assert.All(handler.Requests.Skip(4), request =>
        {
            Assert.Equal("https://api.meta.ai/muse-code/key", request.Url);
            Assert.Equal("Bearer identity-token", request.Authorization);
            Assert.Equal("{}", request.Body);
            Assert.Equal("1.0.0", request.ApiVersion);
            Assert.Equal("application/json", request.ContentType);
        });
    }

    /// <summary>【AI】【Meta OAuth 测试】完整验证地址不是 HTTP 时回退普通地址，并使用默认轮询间隔。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task DeviceUrl_FallsBackAndDefaultsToFiveSeconds()
    {
        using var handler = new ScriptedHandler((200, """{"device_code":"device","user_code":"USER","verification_uri":"https://auth.meta.com/device","verification_uri_complete":"javascript:bad","interval":0,"expires_in":-1}"""),
            (200, """{"access_token":"identity"}"""), (200, """{"api_key":"key"}"""));
        using var client = new HttpClient(handler); var time = new TestClock(); var callbacks = new DeviceCallbacks();
        await new MetaOAuthProvider(client, time.Clock, TimeSpan.FromSeconds(5)).LoginAsync(callbacks);
        Assert.Equal(5000, Assert.Single(time.Waits));
        Assert.Equal(("USER", "https://auth.meta.com/device", (int?)null, (int?)null), Assert.Single(callbacks.Devices));
    }

    /// <summary>【AI】【Meta OAuth 测试】非法设备响应不进入轮询或铸造阶段。</summary>
    /// <param name="body">响应文本。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"device_code\":\"\",\"user_code\":\"USER\",\"verification_uri\":\"https://valid.invalid\"}")]
    [InlineData("{\"device_code\":\"device\",\"user_code\":\"USER\",\"verification_uri\":\"file:///tmp/bad\"}")]
    public async Task InvalidDeviceResponse_StopsLogin(string body)
    {
        using var handler = new ScriptedHandler((200, body)); using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MetaOAuthProvider(client).LoginAsync(new DeviceCallbacks()));
        Assert.StartsWith("Invalid Meta device authorization response:", error.Message);
        Assert.Single(handler.Requests);
    }

    /// <summary>【AI】【Meta OAuth 测试】设备服务错误使用首个非空诊断字段。</summary>
    /// <param name="body">响应文本。</param><param name="detail">预期详情。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("{\"error_description\":\" first \",\"detail\":\"second\"}", ": first")]
    [InlineData("{\"error_description\":\" \",\"detail\":\"second\"}", ": second")]
    [InlineData("{\"message\":\"third\"}", ": third")]
    [InlineData("{\"error\":\"last\"}", ": last")]
    [InlineData("not-json", "")]
    public async Task DeviceError_PreservesUsefulDetail(string body, string detail)
    {
        using var handler = new ScriptedHandler((403, body)); using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MetaOAuthProvider(client).LoginAsync(new DeviceCallbacks()));
        Assert.Equal("Meta device authorization failed with status 403" + detail, error.Message);
    }

    /// <summary>【AI】【Meta OAuth 测试】拒绝、过期及未知轮询错误不会继续铸造密钥。</summary>
    /// <param name="status">状态。</param><param name="body">响应。</param><param name="message">诊断。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(400, "{\"error\":\"access_denied\"}", "Meta login was denied.")]
    [InlineData(400, "{\"error\":\"expired_token\"}", "Meta device authorization expired. Please restart login.")]
    [InlineData(503, "{\"detail\":\"maintenance\"}", "Meta device token request failed with status 503: maintenance")]
    [InlineData(200, "{\"access_token\":\"\"}", "Meta device token request failed with status 200")]
    public async Task TokenError_StopsBeforeMint(int status, string body, string message)
    {
        using var handler = new ScriptedHandler((200, Device), (status, body)); using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MetaOAuthProvider(client, new TestClock().Clock, TimeSpan.FromSeconds(5)).LoginAsync(new DeviceCallbacks()));
        Assert.Equal(message, error.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>【AI】【Meta OAuth 测试】身份失效要求重新登录，缺少密钥时只展示可信的设置地址。</summary>
    /// <param name="status">状态。</param><param name="body">响应。</param><param name="message">诊断。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(401, "{\"detail\":\"expired\"}", "Meta session expired (status 401). Run `/login meta` to sign in again.: expired")]
    [InlineData(403, "{}", "Meta session expired (status 403). Run `/login meta` to sign in again.")]
    [InlineData(500, "{\"message\":\"busy\"}", "Meta API key mint failed with status 500: busy")]
    [InlineData(200, "{}", "Meta did not issue an API key.")]
    [InlineData(200, "{\"action_url\":\"https://meta.invalid/setup\"}", "Meta did not issue an API key. Complete setup at https://meta.invalid/setup")]
    [InlineData(200, "{\"action_url\":\"javascript:bad\"}", "Meta did not issue an API key.")]
    public async Task RefreshFailure_ReportsRequiredAction(int status, string body, string message)
    {
        using var handler = new ScriptedHandler((status, body)); using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MetaOAuthProvider(client).RefreshTokenAsync(new OAuthCredentials { Access = "old-key", Refresh = "identity", ExpiresAt = DateTimeOffset.UtcNow }));
        Assert.Equal(message, error.Message);
        Assert.Equal("https://api.meta.ai/muse-code/key", Assert.Single(handler.Requests).Url);
    }

    /// <summary>【AI】【Meta OAuth 测试】请求期限与调用方取消都能结束正在执行的登录。</summary>
    /// <param name="callerCancel">是否由调用方取消。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTimeout_StopRequests(bool callerCancel)
    {
        using var source = new CancellationTokenSource();
        using var handler = new ScriptedHandler { Blocking = token => { if (callerCancel) source.Cancel(); return Task.Delay(Timeout.Infinite, token); } };
        using var client = new HttpClient(handler);
        var provider = new MetaOAuthProvider(client, new TestClock().Clock, TimeSpan.FromMilliseconds(30));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.LoginAsync(new DeviceCallbacks(), source.Token));
        if (callerCancel) Assert.Equal("Login cancelled", error.Message);
        Assert.Single(handler.Requests);
    }

    /// <summary>【AI】【Meta OAuth 测试】已取消的登录不会发出申请请求。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task PreCancelled_DoesNotRequest()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        using var handler = new ScriptedHandler(); using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MetaOAuthProvider(client).LoginAsync(new DeviceCallbacks(), source.Token));
        Assert.Empty(handler.Requests);
    }

    /// <summary>【AI】【Meta OAuth 测试】保留请求的必要协议字段。</summary>
    private sealed record Request(string Url, string Body, string? Authorization, string? ContentType, string? ApiVersion);

    /// <summary>【AI】【Meta OAuth 测试】按序返回合成响应，所有网络请求都被拦截。</summary>
    private sealed class ScriptedHandler(params (int Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> _responses = new(responses);
        public List<Request> Requests { get; } = [];
        public Func<CancellationToken, Task>? Blocking { get; init; }
        /// <summary>记录请求并返回下一项脚本响应。</summary><param name="request">请求。</param><param name="token">取消。</param><returns>响应。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains(request.Headers.Accept, item => item.MediaType == "application/json");
            Requests.Add(new(request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(token), request.Headers.Authorization?.ToString(),
                request.Content.Headers.ContentType!.MediaType, request.Headers.TryGetValues("x-api-version", out var version) ? version.Single() : null));
            if (Blocking is not null) await Blocking(token);
            var response = _responses.Dequeue();
            return new HttpResponseMessage((HttpStatusCode)response.Status) { Content = new StringContent(response.Body) };
        }
    }

    /// <summary>【AI】【Meta OAuth 测试】立即推进时间，避免等待实际轮询间隔。</summary>
    private sealed class TestClock
    {
        public long Now { get; private set; } = 2000000000000L;
        public List<double> Waits { get; } = [];
        public OAuthFlowClock Clock { get; }
        /// <summary>绑定测试时间。</summary>
        public TestClock() => Clock = new OAuthFlowClock { NowMilliseconds = () => Now, SleepAsync = SleepAsync };
        /// <summary>推进并记录等待。</summary><param name="milliseconds">毫秒。</param><param name="token">信号。</param><returns>完成任务。</returns>
        private Task SleepAsync(double milliseconds, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Waits.Add(milliseconds); Now += (long)milliseconds; return Task.CompletedTask; }
    }

    /// <summary>【AI】【Meta OAuth 测试】记录设备码和进度，不接受额外输入。</summary>
    private sealed class DeviceCallbacks : IOAuthLoginCallbacks
    {
        public List<(string Code, string Uri, int? Interval, int? Expires)> Devices { get; } = [];
        public List<string> Progress { get; } = [];
        /// <summary>设备认证不使用单独链接提示。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) => throw new NotSupportedException();
        /// <summary>记录设备通知。</summary><param name="userCode">代码。</param><param name="verificationUri">地址。</param><param name="intervalSeconds">间隔。</param><param name="expiresInSeconds">期限。</param>
        public void OnDeviceCode(string userCode, string verificationUri, int? intervalSeconds = null, int? expiresInSeconds = null) => Devices.Add((userCode, verificationUri, intervalSeconds, expiresInSeconds));
        /// <summary>记录进度。</summary><param name="message">消息。</param>
        public void OnProgress(string message) => Progress.Add(message);
        /// <summary>此流程无提示输入。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">空值策略。</param><returns>未使用。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new NotSupportedException();
        /// <summary>此流程无手动代码。</summary><returns>空。</returns>
        public Task<string>? OnManualCodeInputAsync() => null;
    }
}
