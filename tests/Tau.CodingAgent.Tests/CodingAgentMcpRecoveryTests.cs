// 作者：xxx
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpRecoveryTests
{
    /// <summary>【CodingAgent】【MCP 并发恢复】初始化暂时失败后重试，八个旧会话请求共用一次重连，旧连接不得提前关闭。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpRecoverySharesReconnectAndDoesNotCancelOtherExpiredRequests()
    {
        await using var fixture = new RecoveryFixture();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStarted = 0; var oldFinished = 0;
        fixture.Intercept = async (method, message, request, token) =>
        {
            if (method == "initialize" && fixture.Initializations == 1) return new(HttpStatusCode.ServiceUnavailable);
            if (method == "tools/call" && Session(request) == "session-2")
            {
                var index = Interlocked.Increment(ref oldStarted);
                if (index == 8) arrived.TrySetResult();
                await arrived.Task.WaitAsync(token);
                if (index > 1) await Task.Delay(80, token);
                Assert.Equal(0, fixture.Deletes);
                Interlocked.Increment(ref oldFinished);
                return new(HttpStatusCode.NotFound);
            }
            return null;
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.ConnectAsync(deadline.Token);
        Assert.Equal(2, fixture.Initializations);
        var tool = Assert.Single(fixture.Service.GetTools());
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => tool.ExecuteAsync(index.ToString(), Json("{}"), deadline.Token)));
        Assert.All(results, result => Assert.False(result.IsError));
        Assert.Equal(8, oldStarted); Assert.Equal(8, oldFinished);
        Assert.Equal(3, fixture.Initializations); Assert.Equal(3, fixture.Transports); Assert.Equal(16, fixture.Calls);
        Assert.Equal(1, fixture.Deletes);
        Assert.Equal("connected", Assert.Single(fixture.Service.GetStatus()).State);
    }

    /// <summary>【CodingAgent】【MCP 重试边界】资源瞬态错误重试一次，工具和普通协议错误不自动重放，认证失效转换状态。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpRecoveryRetriesOnlyReadOperationsAndMarksAuthenticationFailures()
    {
        await using var fixture = new RecoveryFixture();
        var mode = "resource"; var attempted = 0;
        fixture.Intercept = (method, message, request, token) =>
        {
            HttpResponseMessage? response = null;
            if (method == "resources/read" && mode == "resource" && Interlocked.Increment(ref attempted) == 1) response = new(HttpStatusCode.ServiceUnavailable);
            if (method == "tools/call")
                response = mode switch
                {
                    "transient" => new(HttpStatusCode.ServiceUnavailable),
                    "auth" => new(HttpStatusCode.Unauthorized),
                    "protocol" => Reply(message, new JsonObject { ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "no such tool" } }),
                    _ => null
                };
            return Task.FromResult(response);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await fixture.ConnectAsync(deadline.Token);
        var resource = Assert.Single(fixture.Service.GetResourceTools(), tool => tool.Name == "read_mcp_resource");
        Assert.False((await resource.ExecuteAsync("read", Json("""{"server":"http","uri":"fixture://read"}"""), deadline.Token)).IsError);
        Assert.Equal(2, fixture.Reads); Assert.Equal(1, fixture.Initializations);
        var tool = Assert.Single(fixture.Service.GetTools()); mode = "transient";
        await Assert.ThrowsAsync<CodingAgentMcpHttpException>(() => tool.ExecuteAsync("mutate", Json("{}"), deadline.Token));
        Assert.Equal(1, fixture.Calls);
        mode = "protocol";
        await Assert.ThrowsAsync<CodingAgentMcpException>(() => tool.ExecuteAsync("bad", Json("{}"), deadline.Token));
        Assert.Equal(2, fixture.Calls); Assert.Equal(1, fixture.Initializations);
        mode = "auth";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync("auth", Json("{}"), deadline.Token));
        Assert.Contains("requires sign-in", error.Message); Assert.Equal("needs-auth", Assert.Single(fixture.Service.GetStatus()).State);
        Assert.Null(Assert.Single(fixture.Service.GetStatus()).Error);
        mode = "ok";
        Assert.False((await tool.ExecuteAsync("again", Json("{}"), deadline.Token)).IsError);
        Assert.Equal(2, fixture.Initializations); Assert.Equal("connected", Assert.Single(fixture.Service.GetStatus()).State);
    }

    /// <summary>【CodingAgent】【旧认证失败】旧会话的迟到认证失败不得覆盖另一请求已经恢复的连接状态。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpLateAuthenticationFailureCannotOverwriteRecoveredConnection()
    {
        await using var fixture = new RecoveryFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.Intercept = async (method, message, request, token) =>
        {
            if (method != "tools/call" || Session(request) != "session-1") return null;
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult(); await release.Task.WaitAsync(token);
                return new(HttpStatusCode.Unauthorized);
            }
            return new(HttpStatusCode.NotFound);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.ConnectAsync(deadline.Token);
        var tool = Assert.Single(fixture.Service.GetTools());
        var stale = tool.ExecuteAsync("stale", Json("{}"), deadline.Token);
        await started.Task.WaitAsync(deadline.Token);
        try
        {
            Assert.False((await tool.ExecuteAsync("recover", Json("{}"), deadline.Token)).IsError);
            Assert.Equal(2, fixture.Initializations);
            Assert.Equal("connected", Assert.Single(fixture.Service.GetStatus()).State);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => stale);
        Assert.Equal("connected", Assert.Single(fixture.Service.GetStatus()).State);
        Assert.Null(Assert.Single(fixture.Service.GetStatus()).Error);
        Assert.False((await tool.ExecuteAsync("current", Json("{}"), deadline.Token)).IsError);
        Assert.Equal(2, fixture.Initializations); Assert.Equal(1, fixture.Deletes);
    }

    /// <summary>【CodingAgent】【认证快照竞争】凭据文件锁阻塞旧认证快照期间，新连接成功后不能被快照写回覆盖。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpAuthenticationSnapshotCannotOverwriteConnectionCreatedWhileWaiting()
    {
        await using var fixture = new RecoveryFixture();
        fixture.Intercept = (method, message, request, token) => Task.FromResult<HttpResponseMessage?>(
            method == "tools/call" && Session(request) == "session-1" ? new(HttpStatusCode.Unauthorized) : null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.ConnectAsync(deadline.Token);
        var tool = Assert.Single(fixture.Service.GetTools());
        var held = new FileStream(Path.Combine(fixture.DirectoryPath, "mcp-auth.json.tau-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var stale = tool.ExecuteAsync("stale", Json("{}"), deadline.Token);
        try
        {
            Assert.False(stale.IsCompleted);
            Assert.False((await tool.ExecuteAsync("recover", Json("{}"), deadline.Token)).IsError);
            Assert.Equal(2, fixture.Initializations);
            Assert.Equal("connected", Assert.Single(fixture.Service.GetStatus()).State);
        }
        finally { held.Dispose(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => stale);
        Assert.Equal("connected", Assert.Single(fixture.Service.GetStatus()).State);
        Assert.Equal(2, fixture.Initializations); Assert.Equal(1, fixture.Deletes);
    }

    /// <summary>【CodingAgent】【MCP 取消退避】调用取消中断资源重试等待，连接仍然可用。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpRecoveryCancellationInterruptsReadRetryWithoutClosingSession()
    {
        await using var fixture = new RecoveryFixture();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Intercept = (method, message, request, token) =>
        {
            if (method == "resources/read") { first.TrySetResult(); return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.ServiceUnavailable)); }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await fixture.ConnectAsync(deadline.Token);
        using var cancellation = new CancellationTokenSource();
        var tool = Assert.Single(fixture.Service.GetResourceTools(), value => value.Name == "read_mcp_resource");
        var pending = tool.ExecuteAsync("cancel", Json("""{"server":"http","uri":"fixture://read"}"""), cancellation.Token);
        await first.Task.WaitAsync(deadline.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, fixture.Reads); Assert.Equal(1, fixture.Initializations); Assert.Equal("connected", Assert.Single(fixture.Service.GetStatus()).State);
    }

    /// <summary>【CodingAgent】【MCP 初始化退避】仅暂时状态和网络错误允许有限重连，不支持或配置错误立即失败。</summary>
    /// <param name="status">模拟状态，零表示网络异常。</param><param name="attempts">预期连接次数。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(503, 3)]
    [InlineData(429, 3)]
    [InlineData(501, 1)]
    [InlineData(404, 1)]
    [InlineData(0, 3)]
    public async Task McpRecoveryUsesBoundedConnectRetries(int status, int attempts)
    {
        await using var fixture = new RecoveryFixture();
        fixture.Intercept = (method, message, request, token) =>
        {
            if (method == "initialize")
            {
                if (status == 0) throw new HttpRequestException("fixture network failure");
                return Task.FromResult<HttpResponseMessage?>(new((HttpStatusCode)status));
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await fixture.ConnectAsync(deadline.Token);
        Assert.Equal(attempts, fixture.Initializations); Assert.Equal(attempts, fixture.Transports);
        Assert.Equal("failed", Assert.Single(fixture.Service.GetStatus()).State);
    }

    /// <summary>【CodingAgent】【MCP 会话头】读取测试请求携带的会话标识。</summary><param name="request">HTTP 请求。</param><returns>标识或空值。</returns>
    private static string? Session(HttpRequestMessage request) => request.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.Single() : null;
    /// <summary>【CodingAgent】【MCP JSON】创建独立协议元素。</summary><param name="json">文本。</param><returns>独立元素。</returns>
    private static JsonElement Json(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    /// <summary>【CodingAgent】【MCP 测试响应】将结果或错误包装为匹配请求的 JSON-RPC 响应。</summary><param name="request">请求。</param><param name="body">result/error 字段。</param><returns>HTTP 响应。</returns>
    private static HttpResponseMessage Reply(JsonElement request, JsonObject body)
    {
        body["jsonrpc"] = "2.0"; body["id"] = JsonNode.Parse(request.GetProperty("id").GetRawText());
        return new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    /// <summary>【CodingAgent】【MCP 恢复夹具】通过真实 HTTP 传输类注入确定性服务器响应，不访问外部服务。</summary>
    private sealed class RecoveryFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "tau-mcp-recovery-" + Guid.NewGuid().ToString("N"));
        internal string DirectoryPath => _directory;
        private readonly HttpClient _http;
        internal readonly CodingAgentMcpService Service;
        internal Func<string, JsonElement, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Intercept;
        internal int Initializations, Calls, Reads, Deletes, Transports;

        /// <summary>【CodingAgent】【MCP 恢复设置】创建配置与注入传输，后台通知流关闭以便只检查请求恢复。</summary>
        internal RecoveryFixture()
        {
            Directory.CreateDirectory(_directory); _http = new(new RecoveryHandler(RespondAsync));
            CodingAgentMcpConfiguration.Add(Path.Combine(_directory, "mcp.json"), "http", new() { ["url"] = "http://fixture.test/mcp", ["exposure"] = "direct" });
            Service = new(_directory, _directory, () => false, new(), new()
            {
                TransportFactory = (entry, cwd, token) =>
                {
                    Interlocked.Increment(ref Transports);
                    return Task.FromResult<ICodingAgentMcpTransport>(new CodingAgentMcpHttpTransport(new Uri("http://fixture.test/mcp"), _http, openGetStream: false));
                }
            });
        }
        /// <summary>【CodingAgent】【MCP 测试连接】等候配置和首次连接尝试完成。</summary><param name="token">截止信号。</param><returns>连接任务。</returns>
        internal async Task ConnectAsync(CancellationToken token) { await Service.ReloadAsync(token); await Service.WaitForServersAsync(token); }
        /// <summary>【CodingAgent】【MCP 协议模拟】处理初始化、工具发现、工具执行和资源读取。</summary><param name="request">HTTP 请求。</param><param name="token">取消信号。</param><returns>协议响应。</returns>
        private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Delete) { Interlocked.Increment(ref Deletes); return new(HttpStatusCode.OK); }
            var message = Json(await request.Content!.ReadAsStringAsync(token)); var method = message.GetProperty("method").GetString()!;
            if (method == "initialize") Interlocked.Increment(ref Initializations);
            if (method == "tools/call") Interlocked.Increment(ref Calls);
            if (method == "resources/read") Interlocked.Increment(ref Reads);
            if (Intercept is { } intercept && await intercept(method, message, request, token) is { } custom) return custom;
            if (!message.TryGetProperty("id", out _)) return new(HttpStatusCode.Accepted);
            var result = method switch
            {
                "initialize" => new JsonObject { ["protocolVersion"] = CodingAgentMcpClient.LatestProtocolVersion, ["serverInfo"] = new JsonObject { ["name"] = "fixture", ["version"] = "1" }, ["capabilities"] = new JsonObject { ["tools"] = new JsonObject(), ["resources"] = new JsonObject() } },
                "tools/list" => JsonNode.Parse("""{"tools":[{"name":"action","inputSchema":{"type":"object"}}]}""")!,
                "tools/call" => JsonNode.Parse("""{"content":[{"type":"text","text":"done"}]}""")!,
                "resources/read" => JsonNode.Parse("""{"contents":[{"uri":"fixture://read","text":"resource"}]}""")!,
                _ => new JsonObject()
            };
            var response = Reply(message, new JsonObject { ["result"] = result });
            if (method == "initialize") response.Headers.Add("Mcp-Session-Id", "session-" + Initializations);
            return response;
        }
        /// <summary>【CodingAgent】【MCP 夹具释放】先关闭连接，再释放注入客户端和本夹具目录。</summary><returns>清理任务。</returns>
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); _http.Dispose(); Directory.Delete(_directory, true); }
    }

    /// <summary>【CodingAgent】【MCP 测试传输】把 HTTP 请求交给夹具，覆盖并发与取消而不使用网络。</summary><param name="respond">模拟服务。</param>
    private sealed class RecoveryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        /// <summary>【CodingAgent】【模拟 HTTP 发送】委托夹具生成响应。</summary><param name="request">请求。</param><param name="cancellationToken">取消。</param><returns>响应任务。</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
