// 作者：xxx
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth.OAuth.Providers;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Copilot 登录测试】验证设备响应、共享轮询、企业主机与原生期限。</summary>
public sealed class GitHubCopilotOAuthTests
{
    private const string Device = """{"device_code":"device","user_code":"USER","verification_uri":"https://github.com/login/device","expires_in":120}""";
    private const string GitHubToken = """{"access_token":"github-access"}""";
    private const string CopilotToken = """{"token":"copilot-access","expires_at":2000000000.125}""";

    /// <summary>【AI】【设备码链路】默认间隔、小数通知和限速间隔都经共享轮询器处理，首轮先等待。</summary><param name="interval">可选小数间隔。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(0.125)]
    public async Task Login_UsesTypedNotificationAndSharedPolling(double? interval)
    {
        var device = JsonNode.Parse(Device)!.AsObject(); if (interval is not null) device["interval"] = interval;
        using var handler = new Script((200, device.ToJsonString()), (200, """{"error":"authorization_pending"}"""),
            (200, """{"error":"slow_down","interval":7.5}"""), (200, GitHubToken), (200, CopilotToken));
        using var client = new HttpClient(handler); var time = new Clock(); var callbacks = new Callbacks("");
        var result = await Create(client, time).LoginAsync(callbacks);
        Assert.Equal(new[] { interval is null ? 5000.0 : 1000.0, interval is null ? 5000.0 : 1000.0, 7500.0 }, time.Delays);
        Assert.Equal(interval, callbacks.Device!.IntervalSeconds); Assert.Equal(120.0, callbacks.Device.ExpiresInSeconds);
        Assert.Equal("USER", callbacks.Device.UserCode); Assert.Equal("https://github.com/login/device", callbacks.Device.VerificationUri);
        Assert.Equal("copilot-access", result.Access); Assert.Equal("github-access", result.Refresh); Assert.Equal(1999999700125, result.ExpiresUnixTimeMilliseconds);
        Assert.Empty(result.Metadata); Assert.Equal("read:user", handler.Requests[0].Fields["scope"]);
        Assert.All(handler.Requests.Skip(1).Take(3), request => Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", request.Fields["grant_type"]));
        Assert.Equal("Bearer github-access", handler.Requests[^2].Authorization);
        Assert.Equal("vscode/1.107.0", handler.Requests[^2].Editor); Assert.All(handler.Requests, request => Assert.Equal("GitHubCopilotChat/0.35.0", request.UserAgent));
    }

    /// <summary>【AI】【不可信 URI】验证地址只允许 HTTP(S)，失败时不发出通知或继续轮询。</summary><param name="uri">地址。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("file:///C:/tool.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("relative/path")]
    public async Task Login_RejectsUntrustedVerificationUri(string uri)
    {
        var device = JsonNode.Parse(Device)!.AsObject(); device["verification_uri"] = uri;
        using var handler = new Script((200, device.ToJsonString())); using var client = new HttpClient(handler); var callbacks = new Callbacks("");
        Assert.Equal("Untrusted verification_uri in device code response", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(callbacks))).Message);
        Assert.Null(callbacks.Device); Assert.Single(handler.Requests);
    }

    /// <summary>【AI】【设备字段校验】无效顶层、遗漏字段及显式无效间隔均报告稳定错误。</summary><param name="json">响应。</param><param name="message">错误。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("null", "Invalid device code response")]
    [InlineData("[]", "Invalid device code response")]
    [InlineData("{}", "Invalid device code response fields")]
    [InlineData("{\"device_code\":1,\"user_code\":\"u\",\"verification_uri\":\"https://x.invalid\",\"expires_in\":120}", "Invalid device code response fields")]
    [InlineData("{\"device_code\":\"d\",\"user_code\":\"u\",\"verification_uri\":\"https://x.invalid\",\"expires_in\":120,\"interval\":null}", "Invalid device code response fields")]
    [InlineData("{\"device_code\":\"d\",\"user_code\":\"u\",\"verification_uri\":\"https://x.invalid\",\"expires_in\":\"120\"}", "Invalid device code response fields")]
    public async Task InvalidDeviceResponse_StopsLogin(string json, string message)
    {
        using var handler = new Script((200, json)); using var client = new HttpClient(handler);
        Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(new Callbacks("")))).Message);
    }

    /// <summary>【AI】【轮询错误】非成功 HTTP、协议拒绝和无效 JSON 结构都立即结束登录。</summary><param name="status">状态码。</param><param name="body">正文。</param><param name="message">诊断片段。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(400, "{\"error\":\"authorization_pending\"}", "400")]
    [InlineData(200, "{\"error\":\"access_denied\",\"error_description\":\"Denied\"}", "Device flow failed: access_denied: Denied")]
    [InlineData(200, "{}", "Invalid device token response")]
    [InlineData(200, "null", "Invalid device token response")]
    public async Task PollFailure_IsTerminal(int status, string body, string message)
    {
        using var handler = new Script((200, Device), (status, body)); using var client = new HttpClient(handler);
        Assert.Contains(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(new Callbacks("")))).Message); Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>【AI】【企业凭据】完整企业 URL 在登录、刷新和请求端点派生时都转换为主机名。</summary><param name="native">是否存于原生 JSON 字段。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_NormalizesStoredEnterpriseUrl(bool native)
    {
        using var handler = new Script((200, CopilotToken)); using var client = new HttpClient(handler); var provider = Create(client);
        var old = new OAuthCredentials { Refresh = "github-refresh", Access = "old", ExpiresAt = DateTimeOffset.UnixEpoch };
        if (native) { using var json = JsonDocument.Parse("\"https://company.ghe.com/path\""); old = old with { Properties = new Dictionary<string, JsonElement> { ["enterpriseUrl"] = json.RootElement.Clone() } }; }
        else old = old with { Metadata = new Dictionary<string, string> { ["enterpriseUrl"] = "https://company.ghe.com/path" } };
        Assert.Equal("https://copilot-api.company.ghe.com", provider.ResolveAuth(old).BaseUrl);
        var result = await provider.RefreshTokenAsync(old);
        Assert.Equal("https://api.company.ghe.com/copilot_internal/v2/token", handler.Requests[0].Url);
        Assert.Equal("https://copilot-api.company.ghe.com/models", handler.Requests[1].Url);
        Assert.Equal("company.ghe.com", result.Metadata["enterpriseUrl"]);
    }

    /// <summary>【AI】【兑换校验】非对象或错误字段类型不能构成访问令牌。</summary><param name="body">正文。</param><param name="message">错误。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("null", "Invalid Copilot token response")]
    [InlineData("{}", "Invalid Copilot token response fields")]
    [InlineData("{\"token\":42,\"expires_at\":2000000000}", "Invalid Copilot token response fields")]
    [InlineData("{\"token\":\"access\",\"expires_at\":\"2000000000\"}", "Invalid Copilot token response fields")]
    public async Task InvalidTokenResponse_IsRejected(string body, string message)
    {
        using var handler = new Script((200, body)); using var client = new HttpClient(handler);
        Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).RefreshTokenAsync(new() { Refresh = "refresh", Access = "old", ExpiresAt = DateTimeOffset.UnixEpoch }))).Message);
    }

    /// <summary>【AI】【登录取消】域名提示忽略取消时也停止等待，且不发出 HTTP 请求。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task PromptCancellation_StopsBeforeNetwork()
    {
        using var source = new CancellationTokenSource(); var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Script(); using var client = new HttpClient(handler); var callbacks = new Callbacks("") { Prompt = release.Task };
        var pending = Create(client).LoginAsync(callbacks, source.Token); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Empty(handler.Requests); release.TrySetResult("");
    }

    /// <summary>【AI】【测试实现】使用虚拟时钟避免实际等待设备轮询。</summary><param name="client">模拟客户端。</param><param name="clock">可选时钟。</param><returns>认证实现。</returns>
    private static GitHubCopilotOAuthProvider Create(HttpClient client, Clock? clock = null) => new(client, (clock ?? new Clock()).Value);

    /// <summary>【AI】【测试时间】记录并推进所有设备等待。</summary>
    private sealed class Clock
    {
        public double Now { get; private set; } = 1000000;
        public List<double> Delays { get; } = [];
        public OAuthFlowClock Value => new() { NowMilliseconds = () => Now, SleepAsync = (milliseconds, token) => { token.ThrowIfCancellationRequested(); Delays.Add(milliseconds); Now += milliseconds; return Task.CompletedTask; } };
    }

    /// <summary>【AI】【测试交互】只记录类型化设备通知，旧授权通知不应被调用。</summary><param name="input">域名输入。</param>
    private sealed class Callbacks(string input) : IOAuthLoginCallbacks
    {
        public OAuthDeviceCodeNotification? Device { get; private set; }
        public Task<string>? Prompt { get; init; }
        /// <inheritdoc />
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => Prompt ?? Task.FromResult(input);
        /// <inheritdoc />
        public void OnDeviceCode(OAuthDeviceCodeNotification notification) => Device = notification;
        /// <inheritdoc />
        public void OnAuth(string url, string? instructions = null) => throw new InvalidOperationException("Expected typed device notification");
        /// <inheritdoc />
        public void OnProgress(string message) { }
        /// <inheritdoc />
        public Task<string>? OnManualCodeInputAsync() => null;
    }

    /// <summary>【AI】【请求快照】仅含测试合成请求数据。</summary>
    private sealed record Request(string Url, string? Authorization, string? UserAgent, string? Editor, IReadOnlyDictionary<string, string> Fields);

    /// <summary>【AI】【测试 HTTP】按脚本返回响应并捕获表单及协议头。</summary><param name="responses">脚本响应。</param>
    private sealed class Script(params (int Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> _responses = new(responses);
        public List<Request> Requests { get; } = [];
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var fields = request.Content is null ? new Dictionary<string, string>() : OAuthLoopbackServer<string>.Query(await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Headers.UserAgent.ToString(),
                request.Headers.TryGetValues("Editor-Version", out var editor) ? editor.Single() : null, fields));
            if (_responses.Count == 0 && request.RequestUri.AbsolutePath == "/models") return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") };
            var response = _responses.Dequeue(); return new((HttpStatusCode)response.Status) { Content = new StringContent(response.Body) };
        }
    }
}
