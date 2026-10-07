// 作者：xxx
using System.Net;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth.OAuth.Providers;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Kimi Code 测试】核验设备码、Bearer 认证与有界刷新重试。</summary>
public sealed class KimiCodingOAuthTests
{
    private const string Device = """{"device_code":"device","user_code":"USER","verification_uri":"https://auth.kimi.com/device","verification_uri_complete":"https://auth.kimi.com/device?code=USER","interval":2,"expires_in":120}""";
    private const string Token = """{"access_token":"access","refresh_token":"rotated","expires_in":3600}""";

    /// <summary>【AI】【Kimi Code 测试】完整登录遵守首次等待与限速，令牌派生只返回 Bearer 请求头。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Login_PollsWithPkceIndependentDeviceGrantAndUsesBearer()
    {
        using var script = new Script((200, Device), (400, """{"error":"authorization_pending"}"""),
            (400, """{"error":"slow_down"}"""), (200, Token));
        using var client = new HttpClient(script); var time = new Time(); var callbacks = new Callbacks();
        var provider = Create(client, time);
        var credential = await provider.LoginAsync(callbacks);
        Assert.Equal(new double[] { 2000, 2000, 7000 }, time.Delays);
        Assert.Equal("access", credential.Access);
        Assert.Equal("rotated", credential.Refresh);
        Assert.Equal(time.Now + 3600000, credential.ExpiresUnixTimeMilliseconds);
        Assert.Equal(("USER", "https://auth.kimi.com/device?code=USER", 2, 120), callbacks.Device);
        Assert.Equal("https://auth.test/api/oauth/device_authorization", script.Requests[0].Url);
        Assert.Equal("17e5f671-d194-4dfb-9706-5516cb48c098", script.Requests[0].Fields["client_id"]);
        Assert.All(script.Requests.Skip(1), request =>
        {
            Assert.Equal("https://auth.test/api/oauth/token", request.Url);
            Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", request.Fields["grant_type"]);
            Assert.Equal("device", request.Fields["device_code"]);
        });
        var auth = provider.ResolveAuth(credential);
        Assert.Null(auth.ApiKey);
        Assert.Equal("Bearer access", auth.Headers!["Authorization"]);
        Assert.Single(auth.Headers);
    }

    /// <summary>【AI】【Kimi Code 测试】缺失或无效数值使用五秒及十五分钟默认值。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task DeviceDefaults_AreApplied()
    {
        using var script = new Script((200, Device.Replace("\"interval\":2", "\"interval\":0").Replace("\"expires_in\":120", "\"expires_in\":-1")), (200, Token));
        using var client = new HttpClient(script); var time = new Time(); var callbacks = new Callbacks();
        await Create(client, time).LoginAsync(callbacks);
        Assert.Equal(5000, Assert.Single(time.Delays));
        Assert.Equal(5, callbacks.Device.Interval);
        Assert.Equal(900, callbacks.Device.Expires);
    }

    /// <summary>【AI】【Kimi Code 测试】不可信或缺失的设备响应不能继续授权。</summary>
    /// <param name="body">设备响应。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"device_code\":\"d\",\"user_code\":\"u\",\"verification_uri\":\"https://valid.invalid\",\"verification_uri_complete\":\"file:///bad\"}")]
    [InlineData("{\"device_code\":\"d\",\"user_code\":\"u\",\"verification_uri\":\"javascript:bad\",\"verification_uri_complete\":\"https://valid.invalid\"}")]
    public async Task InvalidDevice_IsRejected(string body)
    {
        using var script = new Script((200, body)); using var client = new HttpClient(script);
        Assert.StartsWith("Invalid Kimi Code device authorization response:", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(new Callbacks()))).Message);
        Assert.Single(script.Requests);
    }

    /// <summary>【AI】【Kimi Code 测试】轮询终止状态不会触发刷新重试策略。</summary>
    /// <param name="status">HTTP 状态。</param><param name="body">响应。</param><param name="message">诊断。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(400, "{\"error\":\"access_denied\"}", "Kimi Code login was denied.")]
    [InlineData(400, "{\"error\":\"expired_token\"}", "Kimi Code device authorization expired. Please restart login.")]
    [InlineData(503, "busy", "Kimi Code device token request failed with status 503: busy")]
    [InlineData(418, "{\"error\":\"unknown\",\"error_description\":\"detail\"}", "Kimi Code device token request failed (status 418): unknown: detail")]
    public async Task PollFailure_StopsImmediately(int status, string body, string message)
    {
        using var script = new Script((200, Device), (status, body)); using var client = new HttpClient(script);
        Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(new Callbacks()))).Message);
        Assert.Equal(2, script.Requests.Count);
    }

    /// <summary>【AI】【Kimi Code 测试】刷新时采用一、二、四秒退避，429、5xx 与传输错误均可恢复。</summary>
    /// <param name="network">是否模拟网络异常。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_RetriesTransientFailures(bool network)
    {
        using var script = network
            ? new Script(new HttpRequestException("connection lost"), new HttpRequestException("connection lost"), new TaskCanceledException("request timeout"), (200, Token))
            : new Script((429, "{}"), (503, "{}"), (500, "{}"), (200, Token));
        using var client = new HttpClient(script); var time = new Time();
        var credential = await Create(client, time).RefreshTokenAsync(OldCredential());
        Assert.Equal("rotated", credential.Refresh);
        Assert.Equal(new double[] { 1000, 2000, 4000 }, time.Delays);
        Assert.Equal(4, script.Requests.Count);
        Assert.All(script.Requests, request =>
        {
            Assert.Equal("refresh_token", request.Fields["grant_type"]);
            Assert.Equal("old-refresh", request.Fields["refresh_token"]);
        });
    }

    /// <summary>【AI】【Kimi Code 测试】无效授权不重试，提示需要重新登录。</summary>
    /// <param name="status">状态。</param><param name="body">响应。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(401, "{}")]
    [InlineData(403, "{}")]
    [InlineData(400, "{\"error\":\"invalid_grant\",\"error_description\":\"revoked\"}")]
    public async Task Refresh_UnauthorizedDoesNotRetry(int status, string body)
    {
        using var script = new Script((status, body)); using var client = new HttpClient(script); var time = new Time();
        Assert.StartsWith($"Kimi Code token refresh unauthorized (status {status})", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client, time).RefreshTokenAsync(OldCredential()))).Message);
        Assert.Single(script.Requests);
        Assert.Empty(time.Delays);
    }

    /// <summary>【AI】【Kimi Code 测试】刷新失败有明确重试上限。</summary>
    /// <param name="network">是否网络异常。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_RetryBudgetIsBounded(bool network)
    {
        var failure = new HttpRequestException("offline");
        using var script = network ? new Script(failure, failure, failure, failure) : new Script((503, "{}"), (503, "{}"), (503, "{}"), (503, "{}"));
        using var client = new HttpClient(script); var time = new Time();
        if (network) Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => Create(client, time).RefreshTokenAsync(OldCredential())));
        else Assert.Equal("Kimi Code token refresh failed with status 503: {}", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client, time).RefreshTokenAsync(OldCredential()))).Message);
        Assert.Equal(4, script.Requests.Count);
        Assert.Equal(3, time.Delays.Count);
    }

    /// <summary>【AI】【Kimi Code 测试】成功状态中缺少字段不能当成有效刷新结果。</summary>
    /// <param name="body">响应。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"\",\"expires_in\":60}")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"expires_in\":0}")]
    public async Task Refresh_InvalidSuccessDoesNotRetry(string body)
    {
        using var script = new Script((200, body)); using var client = new HttpClient(script);
        Assert.StartsWith("Kimi Code token refresh response missing fields:", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).RefreshTokenAsync(OldCredential()))).Message);
        Assert.Single(script.Requests);
    }

    /// <summary>【AI】【Kimi Code 测试】取消退避不再发送后续刷新请求。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Refresh_CancellationStopsBackoff()
    {
        using var source = new CancellationTokenSource();
        using var script = new Script((503, "{}")); using var client = new HttpClient(script);
        var clock = new OAuthFlowClock { SleepAsync = (_, token) => { source.Cancel(); return Task.Delay(Timeout.Infinite, token); } };
        var provider = new KimiCodingOAuthProvider(client, clock, TimeSpan.FromSeconds(5), "https://auth.test");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RefreshTokenAsync(OldCredential(), source.Token));
        Assert.Single(script.Requests);
    }

    /// <summary>【AI】【Kimi Code 测试】生成已过期的测试凭据。</summary><returns>凭据。</returns>
    private static OAuthCredentials OldCredential() => new() { Access = "old-access", Refresh = "old-refresh", ExpiresAt = DateTimeOffset.UnixEpoch };
    /// <summary>【AI】【Kimi Code 测试】创建隔离认证实现并验证主机尾部斜杠规范化。</summary>
    /// <param name="client">客户端。</param><param name="time">测试时间。</param><returns>提供方。</returns>
    private static KimiCodingOAuthProvider Create(HttpClient client, Time? time = null) => new(client, (time ?? new Time()).Clock, TimeSpan.FromSeconds(5), "https://auth.test///");

    /// <summary>【AI】【Kimi Code 测试】记录地址和解码后的表单。</summary>
    private sealed record Request(string Url, Dictionary<string, string> Fields);
    /// <summary>【AI】【Kimi Code 测试】脚本可返回响应或抛出网络异常。</summary>
    private sealed class Script(params object[] steps) : HttpMessageHandler
    {
        private readonly Queue<object> _steps = new(steps);
        public List<Request> Requests { get; } = [];
        /// <summary>截获全部 HTTP 请求。</summary><param name="request">请求。</param><param name="token">信号。</param><returns>模拟响应。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            Assert.Contains(request.Headers.Accept, item => item.MediaType == "application/json");
            Requests.Add(new(request.RequestUri!.AbsoluteUri, OAuthLoopbackServer<string>.Query(await request.Content.ReadAsStringAsync(token))));
            var step = _steps.Dequeue();
            if (step is Exception error) throw error;
            var (status, body) = ((int, string))step;
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
        }
    }
    /// <summary>【AI】【Kimi Code 测试】虚拟等待时间。</summary>
    private sealed class Time
    {
        public long Now { get; private set; } = 2000000000000L;
        public List<double> Delays { get; } = [];
        public OAuthFlowClock Clock { get; }
        /// <summary>绑定虚拟时间。</summary>
        public Time() => Clock = new OAuthFlowClock { NowMilliseconds = () => Now, SleepAsync = SleepAsync };
        /// <summary>记录等待并前进。</summary><param name="milliseconds">毫秒数。</param><param name="token">信号。</param><returns>完成任务。</returns>
        private Task SleepAsync(double milliseconds, CancellationToken token) { token.ThrowIfCancellationRequested(); Delays.Add(milliseconds); Now += (long)milliseconds; return Task.CompletedTask; }
    }
    /// <summary>【AI】【Kimi Code 测试】仅接受设备码通知。</summary>
    private sealed class Callbacks : IOAuthLoginCallbacks
    {
        public (string Code, string Uri, int? Interval, int? Expires) Device { get; private set; }
        /// <summary>禁止额外授权链接。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) => throw new NotSupportedException();
        /// <summary>记录设备码。</summary><param name="userCode">代码。</param><param name="verificationUri">地址。</param><param name="intervalSeconds">间隔。</param><param name="expiresInSeconds">期限。</param>
        public void OnDeviceCode(string userCode, string verificationUri, int? intervalSeconds = null, int? expiresInSeconds = null) => Device = (userCode, verificationUri, intervalSeconds, expiresInSeconds);
        /// <summary>忽略不影响协议的进度。</summary><param name="message">消息。</param>
        public void OnProgress(string message) { }
        /// <summary>设备码流程没有文本输入。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">空值策略。</param><returns>未使用。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new NotSupportedException();
        /// <summary>设备码流程没有手动代码输入。</summary><returns>空。</returns>
        public Task<string>? OnManualCodeInputAsync() => null;
    }
}
