// 作者：xxx
using System.Net;
using System.Text.Json.Nodes;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth.OAuth.Providers;

namespace Tau.Ai.Tests;

/// <summary>【AI】【xAI 协议测试】用虚拟时间验证设备码通知、HTTP、轮询、刷新和取消。</summary>
public sealed class XaiOAuthTests
{
    private const string Device = """{"device_code":"device","user_code":"USER","verification_uri":"https://auth.x.ai/activate","expires_in":120.5}""";
    private const string Token = """{"access_token":"access","refresh_token":"refresh","expires_in":3600.125}""";

    /// <summary>【AI】【轮询链路】缺省及损坏间隔回退，小数秒保留，slow_down 的有效值可以缩短间隔。</summary>
    /// <param name="rawInterval">JSON 间隔，空引用代表省略。</param><param name="interval">通知中的间隔。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(null, null)]
    [InlineData("null", null)]
    [InlineData("0", null)]
    [InlineData("-2", null)]
    [InlineData("\"2\"", null)]
    [InlineData("1e999", null)]
    [InlineData("0.125", 0.125)]
    [InlineData("2.5", 2.5)]
    public async Task Login_PreservesDeviceNotificationAndSharedPolling(string? rawInterval, double? interval)
    {
        var device = JsonNode.Parse(Device)!.AsObject();
        if (rawInterval is not null) device["interval"] = JsonNode.Parse(rawInterval);
        device["verification_uri_complete"] = "https://AUTH.X.AI/activate?code=USER";
        using var handler = new Script((200, device.ToJsonString()), (400, """{"error":"authorization_pending"}"""),
            (429, """{"error":"slow_down","interval":1.5}"""), (400, """{"error":"slow_down"}"""), (200, Token));
        using var client = new HttpClient(handler); var clock = new Clock(); var callbacks = new Callbacks();
        var provider = new XaiOAuthProvider(client, clock.Value);
        var credential = await provider.LoginAsync(callbacks);
        var initial = interval is null ? 5000 : Math.Max(1000, interval.Value * 1000);
        Assert.Equal(new[] { initial, initial, 1500, 6500 }, clock.Delays);
        Assert.Equal(new OAuthDeviceCodeNotification("USER", "https://auth.x.ai/activate?code=USER", interval, 120.5), callbacks.Device);
        Assert.Equal((long)(clock.Now + 3300125), credential.ExpiresUnixTimeMilliseconds);
        Assert.Equal("access", provider.GetApiKey(credential)); Assert.Equal("refresh", credential.Refresh);
        Assert.Empty(credential.Properties); Assert.Empty(credential.Metadata);
        Assert.Equal("https://auth.x.ai/oauth2/device/code", handler.Requests[0].Url);
        Assert.Equal("pi", handler.Requests[0].Fields["referrer"]);
        Assert.Equal("openid profile email offline_access grok-cli:access api:access", handler.Requests[0].Fields["scope"]);
        Assert.All(handler.Requests, request => { Assert.Equal("application/json", request.Accept); Assert.Equal("application/x-www-form-urlencoded", request.ContentType); Assert.Equal("b1a00492-073a-47ea-816f-4c329264a828", request.Fields["client_id"]); });
        Assert.All(handler.Requests.Skip(1), request => { Assert.Equal("https://auth.x.ai/oauth2/token", request.Url); Assert.Equal("device", request.Fields["device_code"]); Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", request.Fields["grant_type"]); });
    }

    /// <summary>【AI】【验证地址】基础及完整地址都只允许 HTTPS，失败时不得通知或继续轮询。</summary>
    /// <param name="field">地址字段。</param><param name="url">不可信地址。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("verification_uri", "http://auth.x.ai/activate")]
    [InlineData("verification_uri", "file:///C:/tool.exe")]
    [InlineData("verification_uri", "relative/path")]
    [InlineData("verification_uri_complete", "javascript:alert(1)")]
    [InlineData("verification_uri_complete", "http://auth.x.ai/activate")]
    [InlineData("verification_uri_complete", " ")]
    public async Task Login_RejectsUntrustedAddresses(string field, string url)
    {
        var body = JsonNode.Parse(Device)!; body[field] = url;
        using var handler = new Script((200, body.ToJsonString())); using var client = new HttpClient(handler); var callbacks = new Callbacks();
        Assert.Equal("Untrusted verification URI in xAI OAuth response", (await Assert.ThrowsAsync<InvalidOperationException>(() => new XaiOAuthProvider(client, new Clock().Value).LoginAsync(callbacks))).Message);
        Assert.Null(callbacks.Device); Assert.Single(handler.Requests);
    }

    /// <summary>【AI】【设备校验】必需字符串与正数不接受空值或错误类型。</summary>
    /// <param name="field">被破坏字段。</param><param name="raw">字段 JSON。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("device_code", "null")]
    [InlineData("user_code", "\"\"")]
    [InlineData("verification_uri", "42")]
    [InlineData("expires_in", "null")]
    [InlineData("expires_in", "\"120\"")]
    [InlineData("expires_in", "0")]
    [InlineData("expires_in", "-1")]
    [InlineData("expires_in", "1e999")]
    public async Task Login_ValidatesRequiredFields(string field, string raw)
    {
        var body = JsonNode.Parse(Device)!; body[field] = JsonNode.Parse(raw);
        using var handler = new Script((200, body.ToJsonString())); using var client = new HttpClient(handler);
        Assert.Equal("Invalid xAI OAuth response field: " + field, (await Assert.ThrowsAsync<InvalidOperationException>(() => new XaiOAuthProvider(client, new Clock().Value).LoginAsync(new Callbacks()))).Message);
    }

    /// <summary>【AI】【HTTP 校验】无效 JSON 与非对象 JSON 分别产生解析错误和必需字段错误。</summary>
    /// <param name="status">HTTP 状态。</param><param name="body">正文。</param><param name="message">预期诊断。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(200, "not json", "xAI OAuth returned invalid JSON (HTTP 200)")]
    [InlineData(503, "not json", "xAI OAuth returned invalid JSON (HTTP 503)")]
    [InlineData(200, "null", "Invalid xAI OAuth response field: device_code")]
    [InlineData(200, "[]", "Invalid xAI OAuth response field: device_code")]
    [InlineData(200, "true", "Invalid xAI OAuth response field: device_code")]
    [InlineData(403, "{\"error\":\"denied\",\"error_description\":\"reason\"}", "xAI OAuth device authorization failed (HTTP 403): denied: reason")]
    [InlineData(400, "[]", "xAI OAuth device authorization failed (HTTP 400)")]
    public async Task Login_ReportsResponseErrors(int status, string body, string message)
    {
        using var handler = new Script((status, body)); using var client = new HttpClient(handler);
        Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => new XaiOAuthProvider(client, new Clock().Value).LoginAsync(new Callbacks()))).Message);
    }

    /// <summary>【AI】【轮询终止】拒绝、过期及未知错误立即结束；成功 HTTP 不把 error 字段误当等待状态。</summary>
    /// <param name="status">状态。</param><param name="body">响应。</param><param name="message">诊断。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(400, "{\"error\":\"access_denied\"}", "xAI device authorization was denied")]
    [InlineData(400, "{\"error\":\"authorization_denied\"}", "xAI device authorization was denied")]
    [InlineData(400, "{\"error\":\"expired_token\"}", "xAI device code expired")]
    [InlineData(502, "{\"error\":\"server_error\",\"error_description\":\"later\"}", "xAI OAuth device token polling failed (HTTP 502): server_error: later")]
    [InlineData(200, "{\"error\":\"authorization_pending\"}", "Invalid xAI OAuth response field: access_token")]
    public async Task Poll_TerminatesOnErrors(int status, string body, string message)
    {
        using var handler = new Script((200, Device), (status, body)); using var client = new HttpClient(handler);
        Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => new XaiOAuthProvider(client, new Clock().Value).LoginAsync(new Callbacks()))).Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>【AI】【轮询截止】首次等待不超过设备有效期，收到限速后的超时包含时钟同步提示。</summary>
    /// <param name="slow">是否先收到限速。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Poll_StopsAtDeviceDeadline(bool slow)
    {
        var body = JsonNode.Parse(Device)!; body["expires_in"] = slow ? 6.5 : 0.5;
        using var handler = new Script((200, body.ToJsonString()), (400, """{"error":"slow_down"}""")); using var client = new HttpClient(handler); var clock = new Clock();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => new XaiOAuthProvider(client, clock.Value).LoginAsync(new Callbacks()));
        Assert.Equal(slow ? OAuthDeviceCodePoller.SlowDownTimeoutMessage : "Device flow timed out", error.Message);
        Assert.Equal(slow ? new[] { 5000.0, 1500.0 } : [500.0], clock.Delays);
        Assert.Equal(slow ? 2 : 1, handler.Requests.Count);
    }

    /// <summary>【AI】【刷新凭据】只对省略的 refresh_token 和 expires_in 使用缺省值，空白令牌按字符串契约保留。</summary>
    /// <param name="body">成功响应。</param><param name="refresh">期望刷新令牌。</param><param name="expiry">期望原生期限。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("{\"access_token\":\"new\"}", "previous", 4300000L)]
    [InlineData("{\"access_token\":\"new\",\"refresh_token\":\"rotated\",\"expires_in\":0.125}", "rotated", 700125L)]
    [InlineData("{\"access_token\":\" \",\"refresh_token\":\" \"}", " ", 4300000L)]
    [InlineData("{\"access_token\":\"new\",\"expires_in\":1e100}", "previous", long.MaxValue)]
    public async Task Refresh_PreservesValidCredentials(string body, string refresh, long expiry)
    {
        using var handler = new Script((200, body)); using var client = new HttpClient(handler);
        var credential = await new XaiOAuthProvider(client, new Clock().Value).RefreshTokenAsync(Previous());
        Assert.Equal(refresh, credential.Refresh); Assert.Equal(expiry, credential.ExpiresUnixTimeMilliseconds);
        Assert.Equal(OAuthCredentialJson.ClampExpiry(expiry), credential.ExpiresAt);
        Assert.Equal("refresh_token", handler.Requests[0].Fields["grant_type"]); Assert.Equal("previous", handler.Requests[0].Fields["refresh_token"]);
    }

    /// <summary>【AI】【刷新校验】显式错误字段不得静默回退旧刷新令牌或一小时有效期。</summary>
    /// <param name="field">字段。</param><param name="raw">原始 JSON。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("access_token", "null")]
    [InlineData("access_token", "\"\"")]
    [InlineData("refresh_token", "null")]
    [InlineData("refresh_token", "7")]
    [InlineData("refresh_token", "\"\"")]
    [InlineData("expires_in", "null")]
    [InlineData("expires_in", "\"3600\"")]
    [InlineData("expires_in", "0")]
    [InlineData("expires_in", "-1")]
    [InlineData("expires_in", "1e999")]
    public async Task Refresh_RejectsInvalidExplicitFields(string field, string raw)
    {
        var body = JsonNode.Parse(Token)!; body[field] = JsonNode.Parse(raw);
        using var handler = new Script((200, body.ToJsonString())); using var client = new HttpClient(handler);
        Assert.Equal("Invalid xAI OAuth response field: " + field, (await Assert.ThrowsAsync<InvalidOperationException>(() => new XaiOAuthProvider(client, new Clock().Value).RefreshTokenAsync(Previous()))).Message);
    }

    /// <summary>【AI】【刷新拒绝】刷新失败不重试，且保留协议中的拒绝原因。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Refresh_ReportsRejectionWithoutRetry()
    {
        using var handler = new Script((401, """{"error":"invalid_grant","error_description":"revoked"}""")); using var client = new HttpClient(handler);
        Assert.Equal("xAI OAuth token refresh failed (HTTP 401): invalid_grant: revoked", (await Assert.ThrowsAsync<InvalidOperationException>(() => new XaiOAuthProvider(client, new Clock().Value).RefreshTokenAsync(Previous()))).Message);
        Assert.Single(handler.Requests);
    }

    /// <summary>【AI】【取消边界】通知中取消不得继续等待或请求；调用前取消不发送 HTTP。</summary>
    /// <param name="before">是否在登录前取消。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_StopsOnCancellation(bool before)
    {
        using var source = new CancellationTokenSource(); if (before) source.Cancel();
        using var handler = new Script((200, Device)); using var client = new HttpClient(handler); var clock = new Clock();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new XaiOAuthProvider(client, clock.Value).LoginAsync(new Callbacks { Notify = source.Cancel }, source.Token));
        Assert.Equal(before ? 0 : 1, handler.Requests.Count); Assert.Empty(clock.Delays);
    }

    /// <summary>【AI】【取消等待】即使自定义 HTTP 处理器忽略取消，登录和刷新调用也必须及时退出。</summary>
    /// <param name="refresh">是否刷新流程。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_DoesNotWaitForUncooperativeTransport(bool refresh)
    {
        using var source = new CancellationTokenSource(); using var handler = new BlockingHandler(); using var client = new HttpClient(handler);
        var provider = new XaiOAuthProvider(client, new Clock().Value);
        var pending = refresh ? provider.RefreshTokenAsync(Previous(), source.Token) : provider.LoginAsync(new Callbacks(), source.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); source.Cancel();
        try { Assert.Equal("Login cancelled", (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)))).Message); }
        finally { handler.Release.TrySetResult(); await handler.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    /// <summary>【AI】【旧凭据夹具】返回合成刷新令牌。</summary><returns>已过期凭据。</returns>
    private static OAuthCredentials Previous() => new() { Refresh = "previous", Access = "old", ExpiresAt = DateTimeOffset.UnixEpoch };

    /// <summary>【AI】【虚拟时钟】记录等待并同步推进时间。</summary>
    private sealed class Clock
    {
        public double Now { get; private set; } = 1000000;
        public List<double> Delays { get; } = [];
        public OAuthFlowClock Value => new() { NowMilliseconds = () => Now, SleepAsync = (delay, token) => { token.ThrowIfCancellationRequested(); Delays.Add(delay); Now += delay; return Task.CompletedTask; } };
    }

    /// <summary>【AI】【设备交互夹具】只接受类型化通知并允许通知时取消。</summary>
    private sealed class Callbacks : IOAuthLoginCallbacks
    {
        public OAuthDeviceCodeNotification? Device { get; private set; }
        public Action? Notify { get; init; }
        /// <inheritdoc />
        public void OnDeviceCode(OAuthDeviceCodeNotification notification) { Device = notification; Notify?.Invoke(); }
        /// <inheritdoc />
        public void OnAuth(string url, string? instructions = null) => throw new InvalidOperationException("Expected typed notification");
        /// <inheritdoc />
        public void OnProgress(string message) => throw new InvalidOperationException("Unexpected progress notification");
        /// <inheritdoc />
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new InvalidOperationException("Unexpected prompt");
        /// <inheritdoc />
        public Task<string>? OnManualCodeInputAsync() => null;
    }

    /// <summary>【AI】【请求快照】只捕获合成表单及协议头。</summary>
    private sealed record Request(string Url, string? Accept, string? ContentType, IReadOnlyDictionary<string, string> Fields);

    /// <summary>【AI】【HTTP 脚本】按顺序返回模拟响应。</summary><param name="responses">响应列表。</param>
    private sealed class Script(params (int Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> _responses = new(responses);
        public List<Request> Requests { get; } = [];
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Headers.Accept.ToString(), request.Content!.Headers.ContentType?.MediaType,
                OAuthLoopbackServer<string>.Query(await request.Content.ReadAsStringAsync(cancellationToken))));
            var response = _responses.Dequeue(); return new((HttpStatusCode)response.Status) { Content = new StringContent(response.Body) };
        }
    }

    /// <summary>【AI】【阻塞传输夹具】忽略取消，直到测试显式释放。</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult(); await Release.Task; Finished.TrySetResult();
            return new(HttpStatusCode.OK) { Content = new StringContent(Token) };
        }
    }
}
