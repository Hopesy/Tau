// 作者：xxx
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth.OAuth.Providers;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Codex OAuth 测试】验证浏览器、设备授权、令牌与账户 ID 的完整链路。</summary>
public sealed class OpenAICodexOAuthTests
{
    private const string Device = """{"device_auth_id":"device","user_code":"USER","interval":"2.5"}""";
    private const string DeviceCode = """{"authorization_code":"authorization","code_verifier":"device-verifier"}""";

    /// <summary>【AI】【设备码链路】立即首轮、HTTP 等待、嵌套限速、专用重定向与账户字段相互一致。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task DeviceLogin_UsesImmediatePollingAndDeviceExchange()
    {
        using var handler = new Script((200, Device), (403, "not-json"), (404, "not-json"),
            (400, """{"error":"deviceauth_authorization_pending"}"""), (429, """{"error":{"code":"slow_down"},"interval":100}"""), (200, DeviceCode), (200, Token()));
        using var client = new HttpClient(handler); var clock = new Clock(); var callbacks = new Callbacks("device_code");
        handler.CaptureTime = () => clock.Now;
        var provider = Create(client, clock); var result = await provider.LoginAsync(callbacks);
        Assert.Equal(new OAuthDeviceCodeNotification("USER", "https://auth.openai.com/codex/device", 2.5, 900), callbacks.Device);
        Assert.Equal(new[] { 2500.0, 2500.0, 2500.0, 7500.0 }, clock.Delays);
        Assert.Equal(1000000.0, handler.Requests[1].Time);
        var first = handler.Requests[0]; Assert.Equal("https://auth.openai.com/api/accounts/deviceauth/usercode", first.Url);
        Assert.Equal("application/json", first.ContentType); Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", first.Body.GetProperty("client_id").GetString());
        Assert.All(handler.Requests.Skip(1).SkipLast(1), request =>
        { Assert.Equal("https://auth.openai.com/api/accounts/deviceauth/token", request.Url); Assert.Equal("device", request.Body.GetProperty("device_auth_id").GetString()); Assert.Equal("USER", request.Body.GetProperty("user_code").GetString()); });
        var exchange = handler.Requests[^1]; Assert.Equal("https://auth.openai.com/oauth/token", exchange.Url);
        Assert.Equal("application/x-www-form-urlencoded", exchange.ContentType); Assert.Equal("authorization_code", exchange.Fields["grant_type"]);
        Assert.Equal("authorization", exchange.Fields["code"]); Assert.Equal("device-verifier", exchange.Fields["code_verifier"]);
        Assert.Equal("https://auth.openai.com/deviceauth/callback", exchange.Fields["redirect_uri"]);
        Assert.All(handler.Requests, request => Assert.Empty(request.Accept)); Assert.Null(callbacks.Url);
        Assert.Equal("account", result.Metadata["accountId"]); Assert.Equal((long)(clock.Now + 3600125), result.ExpiresUnixTimeMilliseconds);
        Assert.Equal(Jwt("account"), provider.GetApiKey(result)); Assert.Equal("refresh", result.Refresh);
        Assert.Equal("Select OpenAI Codex login method:", callbacks.SelectMessage);
        Assert.Equal(new[] { new OAuthSelectOption("browser", "Browser login (default)"), new OAuthSelectOption("device_code", "Device code login (headless)") }, callbacks.Options);
    }

    /// <summary>【AI】【间隔解析】设备接口的数字字符串按 Number 语义转换，只有有限非负结果有效。</summary>
    /// <param name="raw">JSON。</param><param name="expected">可选秒数。</param>
    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("0.125", 0.125)]
    [InlineData("\" 2.5 \"", 2.5)]
    [InlineData("\"\"", 0.0)]
    [InlineData("\"  \"", 0.0)]
    [InlineData("\"0x10\"", 16.0)]
    [InlineData("\"0O10\"", 8.0)]
    [InlineData("\"0b10\"", 2.0)]
    [InlineData("\"1e2\"", 100.0)]
    [InlineData("\"-0\"", 0.0)]
    [InlineData("null", null)]
    [InlineData("true", null)]
    [InlineData("-1", null)]
    [InlineData("1e999", null)]
    [InlineData("\"Infinity\"", null)]
    [InlineData("\"NaN\"", null)]
    [InlineData("\"2seconds\"", null)]
    [InlineData("\"0x\"", null)]
    [InlineData("\"0b2\"", null)]
    [InlineData("\"-0x10\"", null)]
    public void DeviceInterval_UsesFiniteNonnegativeNumbers(string raw, double? expected)
    {
        using var document = JsonDocument.Parse(raw); Assert.Equal(expected, OpenAICodexOAuthProvider.ParseInterval(document.RootElement));
    }

    /// <summary>【AI】【设备响应】未启用设备服务、非成功 HTTP 及缺失字段都在通知前失败。</summary>
    /// <param name="status">状态。</param><param name="body">正文。</param><param name="message">诊断片段。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(404, "not-json", "device code login is not enabled")]
    [InlineData(500, "server error", "device code request failed with status 500: server error")]
    [InlineData(200, "null", "Invalid OpenAI Codex device code response")]
    [InlineData(200, "[]", "Invalid OpenAI Codex device code response")]
    [InlineData(200, "{}", "Invalid OpenAI Codex device code response")]
    [InlineData(200, "{\"device_auth_id\":\"d\",\"user_code\":\"u\",\"interval\":null}", "Invalid OpenAI Codex device code response")]
    [InlineData(200, "{\"device_auth_id\":\"d\",\"user_code\":\"\",\"interval\":1}", "Invalid OpenAI Codex device code response")]
    public async Task DeviceLogin_RejectsInvalidResponses(int status, string body, string message)
    {
        using var handler = new Script((status, body)); using var client = new HttpClient(handler); var callbacks = new Callbacks("device_code");
        Assert.Contains(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(callbacks))).Message);
        Assert.Null(callbacks.Device); Assert.Single(handler.Requests);
    }

    /// <summary>【AI】【设备终止】未知失败状态与不完整兑换信息立即终止。</summary>
    /// <param name="status">状态。</param><param name="body">正文。</param><param name="message">诊断片段。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(200, "{}", "Invalid OpenAI Codex device auth token response")]
    [InlineData(200, "{\"authorization_code\":\"code\"}", "Invalid OpenAI Codex device auth token response")]
    [InlineData(400, "denied", "device auth failed with status 400: denied")]
    [InlineData(500, "{\"error\":{\"code\":\"server_error\"}}", "device auth failed with status 500")]
    public async Task DevicePolling_StopsOnErrors(int status, string body, string message)
    {
        using var handler = new Script((200, Device), (status, body)); using var client = new HttpClient(handler);
        Assert.Contains(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(new Callbacks("device_code")))).Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>【AI】【设备期限】十五分钟到期后不再轮询；限速后的超时保留虚拟机时钟提示。</summary>
    /// <param name="slow">是否先收到限速。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevicePolling_StopsAtFifteenMinutes(bool slow)
    {
        using var handler = new Script((200, """{"device_auth_id":"d","user_code":"u","interval":900}"""),
            slow ? (429, """{"error":"slow_down"}""") : (403, "")); using var client = new HttpClient(handler); var clock = new Clock();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => Create(client, clock).LoginAsync(new Callbacks("device_code")));
        Assert.Equal(slow ? OAuthDeviceCodePoller.SlowDownTimeoutMessage : "Device flow timed out", error.Message);
        Assert.Equal(new[] { 900000.0 }, clock.Delays); Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>【AI】【等待正文】403 等待响应无需读取正文，应直接释放后进入下一轮。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task DevicePolling_DoesNotReadPendingResponseBody()
    {
        var content = new UnreadableContent();
        using var handler = new Script((200, Device), (403, ""), (200, DeviceCode), (200, Token())) { OverrideContent = index => index == 1 ? content : null };
        using var client = new HttpClient(handler); await Create(client).LoginAsync(new Callbacks("device_code"));
        Assert.False(content.Read); Assert.True(content.Disposed);
    }

    /// <summary>【AI】【浏览器手工回调】端口可用与占用两种情况都保留原生参数及 PKCE，并接受正确 state。</summary>
    /// <param name="busy">是否占用端口。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserLogin_UsesPiParametersAndManualFallback(bool busy)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); if (busy) listener.Start();
        var port = busy ? ((IPEndPoint)listener.LocalEndpoint).Port : 0;
        using var handler = new Script((200, Token())); using var client = new HttpClient(handler);
        var callbacks = new Callbacks("browser") { Input = query => "code#" + query["state"] };
        var result = await Create(client, port: port).LoginAsync(callbacks);
        var query = callbacks.Query; Assert.Equal("pi", query["originator"]); Assert.Equal("true", query["codex_cli_simplified_flow"]); Assert.Equal("true", query["id_token_add_organizations"]);
        Assert.Equal("openid profile email offline_access", query["scope"]); Assert.Matches("^[0-9a-f]{32}$", query["state"]);
        var fields = Assert.Single(handler.Requests).Fields;
        Assert.Equal(query["redirect_uri"], fields["redirect_uri"]); Assert.Equal("code", fields["code"]);
        Assert.Equal(query["code_challenge"], Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        Assert.NotEqual(query["state"], fields["code_verifier"]); Assert.True(callbacks.PromptToken.IsCancellationRequested);
        Assert.Equal("account", result.Metadata["accountId"]); Assert.Null(callbacks.Device);
    }

    /// <summary>【AI】【浏览器回调】无效 state 不消费监听，成功后取消手工输入并关闭端口。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task BrowserLogin_AcceptsValidatedCallback()
    {
        using var handler = new Script((200, Token())); using var client = new HttpClient(handler); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var callbacks = new Callbacks("browser"); var login = Create(client).LoginAsync(callbacks, deadline.Token);
        await callbacks.PromptStarted.Task.WaitAsync(deadline.Token); var query = callbacks.Query; using var browser = new HttpClient();
        using var wrong = await browser.GetAsync(query["redirect_uri"] + "?code=c&state=wrong", deadline.Token); Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var valid = await browser.GetAsync(query["redirect_uri"] + "?code=c&state=" + query["state"], deadline.Token); Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal("account", (await login).Metadata["accountId"]); Assert.True(callbacks.PromptToken.IsCancellationRequested);
        using var listener = new TcpListener(IPAddress.Loopback, new Uri(query["redirect_uri"]).Port); listener.Start();
    }

    /// <summary>【AI】【手工校验】错误 state 或空代码不能发送兑换请求。</summary>
    /// <param name="input">输入。</param><param name="message">错误。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("code#wrong", "State mismatch")]
    [InlineData(" ", "Missing authorization code")]
    public async Task BrowserLogin_RejectsInvalidManualInput(string input, string message)
    {
        using var handler = new Script(); using var client = new HttpClient(handler);
        Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(new Callbacks("browser") { Input = _ => input }))).Message);
        Assert.Empty(handler.Requests);
    }

    /// <summary>【AI】【令牌刷新】刷新保留小数、零或负有效期，不添加五分钟余量，并从新令牌更新账户 ID。</summary>
    /// <param name="seconds">有效秒数。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(3600.125)]
    [InlineData(0.0)]
    [InlineData(-1.25)]
    public async Task Refresh_PreservesExpiryAndNewAccount(double seconds)
    {
        using var handler = new Script((200, Token("rotated-account", seconds))); using var client = new HttpClient(handler);
        var credential = await Create(client).RefreshTokenAsync(Previous());
        Assert.Equal((long)(1000000 + seconds * 1000), credential.ExpiresUnixTimeMilliseconds); Assert.Equal("rotated-account", credential.Metadata["accountId"]);
        var fields = Assert.Single(handler.Requests).Fields; Assert.Equal(3, fields.Count); Assert.Equal("refresh_token", fields["grant_type"]); Assert.Equal("previous", fields["refresh_token"]);
    }

    /// <summary>【AI】【令牌校验】缺省、空令牌和错误 expires_in 类型不得构成凭据。</summary>
    /// <param name="body">响应。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"\",\"expires_in\":1}")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"expires_in\":\"1\"}")]
    public async Task TokenResponse_RejectsMissingFields(string body)
    {
        using var handler = new Script((200, body)); using var client = new HttpClient(handler);
        Assert.Contains("OpenAI Codex token refresh response missing fields", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).RefreshTokenAsync(Previous()))).Message);
    }

    /// <summary>【AI】【账户校验】缺少有效账户字段的访问令牌不得保存为登录结果。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task TokenResponse_RequiresAccountId()
    {
        using var handler = new Script((200, """{"access_token":"not-jwt","refresh_token":"r","expires_in":1}""")); using var client = new HttpClient(handler);
        Assert.Equal("Failed to extract accountId from token", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).RefreshTokenAsync(Previous()))).Message);
    }

    /// <summary>【AI】【取消交互】选择、手工输入或设备通知中的取消均不继续发送请求。</summary>
    /// <param name="stage">取消阶段。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("select")]
    [InlineData("browser")]
    [InlineData("device_code")]
    public async Task Login_StopsAfterCancellation(string stage)
    {
        using var source = new CancellationTokenSource(); using var handler = new Script((200, Device)); using var client = new HttpClient(handler);
        var callbacks = new Callbacks(stage == "select" ? "browser" : stage) { BlockSelection = stage == "select", OnDevice = source.Cancel };
        var login = Create(client).LoginAsync(callbacks, source.Token);
        if (stage != "device_code") { await (stage == "select" ? callbacks.SelectStarted.Task : callbacks.PromptStarted.Task).WaitAsync(TimeSpan.FromSeconds(3)); source.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(stage == "device_code" ? 1 : 0, handler.Requests.Count);
    }

    /// <summary>【AI】【未知方式】没有匹配的登录方式时立即失败。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Login_RejectsUnknownMethod()
    {
        using var handler = new Script(); using var client = new HttpClient(handler);
        Assert.Equal("Unknown OpenAI Codex login method: unknown", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(new Callbacks("unknown")))).Message);
        Assert.Empty(handler.Requests);
    }

    /// <summary>【AI】【隔离提供方】创建使用固定时间和独立端口的认证实现。</summary>
    /// <param name="client">客户端。</param><param name="clock">虚拟时钟。</param><param name="port">回调端口。</param><returns>提供方。</returns>
    private static OpenAICodexOAuthProvider Create(HttpClient client, Clock? clock = null, int port = 0) => new(client, (clock ?? new Clock()).Value, port, "127.0.0.1");

    /// <summary>【AI】【令牌夹具】生成仅用于协议测试的未签名 JWT。</summary><param name="account">账户 ID。</param><returns>合成 JWT。</returns>
    private static string Jwt(string account) => "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject
    { ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = account } }.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    /// <summary>【AI】【响应夹具】生成 OAuth 令牌响应。</summary><param name="account">账户。</param><param name="seconds">有效期。</param><returns>响应 JSON。</returns>
    private static string Token(string account = "account", double seconds = 3600.125) => new JsonObject
    { ["access_token"] = Jwt(account), ["refresh_token"] = "refresh", ["expires_in"] = seconds }.ToJsonString();

    /// <summary>【AI】【旧凭据夹具】生成过期旧凭据。</summary><returns>凭据。</returns>
    private static OAuthCredentials Previous() => new() { Access = "old", Refresh = "previous", ExpiresAt = DateTimeOffset.UnixEpoch };

    /// <summary>【AI】【虚拟时钟】通过等待同步推进时间。</summary>
    private sealed class Clock
    {
        public double Now { get; private set; } = 1000000;
        public List<double> Delays { get; } = [];
        public OAuthFlowClock Value => new() { NowMilliseconds = () => Now, SleepAsync = (delay, token) => { token.ThrowIfCancellationRequested(); Delays.Add(delay); Now += delay; return Task.CompletedTask; } };
    }

    /// <summary>【AI】【交互夹具】记录类型化选择、授权链接与设备通知。</summary><param name="method">登录方式。</param>
    private sealed class Callbacks(string method) : IOAuthLoginCallbacks
    {
        public string? Url { get; private set; }
        public string? SelectMessage { get; private set; }
        public IReadOnlyList<OAuthSelectOption>? Options { get; private set; }
        public OAuthDeviceCodeNotification? Device { get; private set; }
        public CancellationToken PromptToken { get; private set; }
        public Func<Dictionary<string, string>, string>? Input { get; init; }
        public Action? OnDevice { get; init; }
        public bool BlockSelection { get; init; }
        public TaskCompletionSource SelectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PromptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, string> Query => OAuthLoopbackServer<string>.Query(new Uri(Url!).Query);
        /// <inheritdoc />
        public void OnAuth(string url, string? instructions = null) => Url = url;
        /// <inheritdoc />
        public void OnDeviceCode(OAuthDeviceCodeNotification notification) { Device = notification; OnDevice?.Invoke(); }
        /// <inheritdoc />
        public Task<string?> OnSelectAsync(string message, IReadOnlyList<OAuthSelectOption> options, CancellationToken token)
        { SelectMessage = message; Options = options; SelectStarted.TrySetResult(); return BlockSelection ? new TaskCompletionSource<string?>().Task : Task.FromResult<string?>(method); }
        /// <inheritdoc />
        public Task<string> OnManualCodeInputAsync(string message, string? placeholder, CancellationToken token)
        { PromptToken = token; PromptStarted.TrySetResult(); return Input is null ? new TaskCompletionSource<string>().Task : Task.FromResult(Input(Query)); }
        /// <inheritdoc />
        public void OnProgress(string message) => throw new InvalidOperationException("Unexpected progress event");
        /// <inheritdoc />
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new InvalidOperationException("Expected typed prompt");
        /// <inheritdoc />
        public Task<string>? OnManualCodeInputAsync() => throw new InvalidOperationException("Expected typed manual prompt");
    }

    /// <summary>【AI】【请求记录】保存合成请求字段。</summary>
    private sealed record Request(string Url, string ContentType, string Accept, JsonElement Body, IReadOnlyDictionary<string, string> Fields, double? Time);

    /// <summary>【AI】【协议脚本】记录表单或 JSON，返回预设响应。</summary><param name="responses">响应序列。</param>
    private sealed class Script(params (int Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> _responses = new(responses);
        public List<Request> Requests { get; } = [];
        public Func<double>? CaptureTime { get; set; }
        public Func<int, HttpContent?>? OverrideContent { get; init; }
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = await request.Content!.ReadAsStringAsync(cancellationToken); var type = request.Content.Headers.ContentType!.MediaType!;
            using var document = type == "application/json" ? JsonDocument.Parse(text) : null;
            var index = Requests.Count;
            Requests.Add(new(request.RequestUri!.AbsoluteUri, type, request.Headers.Accept.ToString(), document?.RootElement.Clone() ?? default,
                type == "application/json" ? new Dictionary<string, string>() : OAuthLoopbackServer<string>.Query(text), CaptureTime?.Invoke()));
            var response = _responses.Dequeue(); return new((HttpStatusCode)response.Status) { Content = OverrideContent?.Invoke(index) ?? new StringContent(response.Body) };
        }
    }

    /// <summary>【AI】【等待正文夹具】读取即失败，记录是否正确跳过并释放。</summary>
    private sealed class UnreadableContent : HttpContent
    {
        public bool Read { get; private set; }
        public bool Disposed { get; private set; }
        /// <inheritdoc />
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { Read = true; throw new IOException("must not read"); }
        /// <inheritdoc />
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        /// <inheritdoc />
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
