// 作者：xxx
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentMcpProtocolTests
{
    /// <summary>【MCP】【协议恢复】无效报文只能报告诊断，不能执行客户端方法或完成请求；合法数值等价标识仍能完成原请求。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpProtocolRejectsMalformedMessagesWithoutConsumingPendingRequest()
    {
        await using var transport = new ProtocolTransport(); await using var client = new CodingAgentMcpClient();
        var diagnostics = new ConcurrentQueue<Exception>(); client.Error += diagnostics.Enqueue;
        var invoked = 0;
        client.SetRequestHandler("hold", (_, _) => { invoked++; return Task.FromResult(Json("{}")); });
        await client.ConnectAsync(transport);
        var pending = client.RequestAsync("fixture"); var request = await transport.Sent.Reader.ReadAsync();
        var id = request.GetProperty("id").GetRawText();
        foreach (var malformed in new[]
        {
            """{"jsonrpc":2,"id":ID,"result":{}}""",
            """{"jsonrpc":"2.0","id":ID,"result":{},"error":{"code":-1,"message":"must not win"}}""",
            """{"jsonrpc":"2.0","id":ID,"error":{"code":"-1","message":"bad"}}""",
            """{"jsonrpc":"2.0","id":ID,"method":null}""",
            """{"jsonrpc":"2.0","id":null,"method":"hold"}""",
            """{"jsonrpc":"2.0","id":{},"method":"hold"}""",
            """{"jsonrpc":"2.0","id":1e400,"method":"hold"}"""
        }) transport.Emit(malformed.Replace("ID", id, StringComparison.Ordinal));
        Assert.False(pending.IsCompleted); Assert.Equal(0, invoked); Assert.Equal(7, diagnostics.Count);
        Assert.All(diagnostics, error => Assert.Equal(-32600, Assert.IsType<CodingAgentMcpException>(error).Code));
        transport.Emit("""{"jsonrpc":"2.0","id":"unknown","result":{}}""");
        Assert.Contains(diagnostics, error => error.Message.Contains("unknown MCP request unknown"));
        Assert.False(pending.IsCompleted);
        transport.Emit("""{"jsonrpc":"2.0","id":IDe0,"method":"hold","result":{"value":"restored"}}""".Replace("ID", id, StringComparison.Ordinal));
        Assert.Equal("restored", (await pending.WaitAsync(TimeSpan.FromSeconds(3))).GetProperty("value").GetString());
        Assert.Equal(0, invoked);

        var failed = client.RequestAsync("large-code"); request = await transport.Sent.Reader.ReadAsync();
        transport.Emit("""{"jsonrpc":"2.0","id":ID,"error":{"code":5000000000.5,"message":"remote","data":{"retry":true}}}"""
            .Replace("ID", request.GetProperty("id").GetRawText(), StringComparison.Ordinal));
        var failure = await Assert.ThrowsAsync<CodingAgentMcpException>(() => failed);
        Assert.Equal(5000000000.5, failure.Code); Assert.True(failure.DataValue!.Value.GetProperty("retry").GetBoolean());
    }

    /// <summary>【MCP】【服务端取消标识】数字格式和字符串转义不同但值相等的标识可取消同一客户端回调。</summary>
    /// <param name="requestId">原请求 JSON 标识。</param><param name="cancelId">取消消息 JSON 标识。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("1.0", "1e0")]
    [InlineData("\"\\u0061\"", "\"a\"")]
    public async Task McpProtocolNormalizesIncomingCancellationIds(string requestId, string cancelId)
    {
        await using var transport = new ProtocolTransport(); await using var client = new CodingAgentMcpClient();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SetRequestHandler("hold", async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); }
            return Json("{}");
        });
        await client.ConnectAsync(transport);
        transport.Emit("""{"jsonrpc":"2.0","id":ID,"method":"hold"}""".Replace("ID", requestId, StringComparison.Ordinal));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        transport.Emit("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":ID}}""".Replace("ID", cancelId, StringComparison.Ordinal));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var response = await transport.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(response.TryGetProperty("result", out _)); Assert.True(CodingAgentMcpJsonRpc.IsResponse(response));
    }

    /// <summary>【MCP】【SSE 响应校验】无效响应不终止原请求，后续合法响应可恢复，否则返回流提前结束错误。</summary>
    /// <param name="followedByValid">是否跟随合法响应。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpProtocolDoesNotTreatMalformedSseEnvelopeAsAnswered(bool followedByValid)
    {
        using var http = new HttpClient(new ProtocolHttp(followedByValid));
        await using var transport = new CodingAgentMcpHttpTransport(new("https://fixture.test/"), http, openGetStream: false);
        await using var client = new CodingAgentMcpClient();
        var diagnostics = new ConcurrentQueue<Exception>(); client.Error += diagnostics.Enqueue;
        await client.ConnectAsync(transport);
        var pending = client.RequestAsync("fixture", timeout: TimeSpan.FromSeconds(3));
        if (followedByValid) Assert.Equal("valid", (await pending).GetProperty("value").GetString());
        else Assert.Contains("stream ended without a response", (await Assert.ThrowsAsync<CodingAgentMcpException>(() => pending)).Message);
        Assert.Contains(diagnostics, error => error is CodingAgentMcpException protocol && protocol.Code == -32600);
    }

    /// <summary>【MCP】【JSON 响应拒绝】普通 HTTP JSON 报文无效时立即失败原请求，即使请求未设置超时也不悬挂。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpProtocolInvalidJsonResponseFailsRequestWithoutWaitingForTimeout()
    {
        using var http = new HttpClient(new ProtocolHttp(false, true));
        await using var transport = new CodingAgentMcpHttpTransport(new("https://fixture.test/"), http, openGetStream: false);
        await using var client = new CodingAgentMcpClient();
        await client.ConnectAsync(transport);
        var failure = await Assert.ThrowsAsync<CodingAgentMcpException>(() => client.RequestAsync("fixture", timeout: TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(-32600, failure.Code);
    }

    /// <summary>【MCP】【协议测试 JSON】构造独立元素。</summary><param name="text">JSON。</param><returns>独立元素。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    /// <summary>【MCP】【协议内存传输】直接注入未校验报文以验证客户端边界，握手自动响应。</summary>
    private sealed class ProtocolTransport : ICodingAgentMcpTransport
    {
        internal Channel<JsonElement> Sent { get; } = Channel.CreateUnbounded<JsonElement>();
        internal Func<JsonElement, CancellationToken, Task>? OnSend { get; set; }
        internal Func<ValueTask>? OnDispose { get; set; }
        public event Action<JsonElement>? Message;
        public event Action<Exception>? Error { add { } remove { } }
        public event Action? Closed;
        /// <summary>【MCP】【内存启动】测试传输立即可用。</summary><param name="token">取消。</param><returns>完成任务。</returns>
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        /// <summary>【MCP】【测试注入】向客户端发布独立报文。</summary><param name="text">JSON 报文。</param>
        internal void Emit(string text) => Message?.Invoke(Json(text));
        /// <summary>【MCP】【内存发送】自动完成握手，其余请求和客户端响应交给测试读取。</summary><param name="message">报文。</param><param name="token">取消。</param><returns>完成任务。</returns>
        public Task SendAsync(JsonElement message, CancellationToken token = default)
        {
            var method = message.TryGetProperty("method", out var name) ? name.GetString() : null;
            if (method == "initialize") Emit("""{"jsonrpc":"2.0","id":ID,"result":{"protocolVersion":"2025-11-25","capabilities":{},"serverInfo":{"name":"fixture","version":"1"}}}"""
                .Replace("ID", message.GetProperty("id").GetRawText(), StringComparison.Ordinal));
            else if (method != "notifications/initialized") Sent.Writer.TryWrite(message.Clone());
            return OnSend?.Invoke(message, token) ?? Task.CompletedTask;
        }
        /// <summary>【MCP】【内存关闭】结束测试消息队列。</summary><returns>完成任务。</returns>
        public ValueTask DisposeAsync() { Sent.Writer.TryComplete(); Closed?.Invoke(); return OnDispose?.Invoke() ?? ValueTask.CompletedTask; }
    }

    /// <summary>【MCP】【SSE 协议夹具】握手使用 JSON，其余请求返回受控的错误与恢复事件。</summary>
    /// <param name="followedByValid">是否追加合法响应。</param><param name="jsonResponse">是否返回普通 JSON 错误正文。</param>
    private sealed class ProtocolHttp(bool followedByValid, bool jsonResponse = false) : HttpMessageHandler
    {
        /// <summary>【MCP】【协议 HTTP 响应】不访问网络，按请求标识生成握手或事件流。</summary><param name="request">请求。</param><param name="cancellationToken">取消。</param><returns>响应。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            if (!message.ContainsKey("id")) return new(HttpStatusCode.Accepted);
            var id = message["id"]!.ToJsonString();
            if (message["method"]!.GetValue<string>() == "initialize")
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"jsonrpc":"2.0","id":ID,"result":{"protocolVersion":"2025-11-25","capabilities":{},"serverInfo":{"name":"fixture","version":"1"}}}"""
                    .Replace("ID", id, StringComparison.Ordinal), Encoding.UTF8, "application/json") };
            var malformed = """{"jsonrpc":"2.0","id":ID,"result":{},"error":{"code":-1,"message":"invalid response"}}""".Replace("ID", id, StringComparison.Ordinal);
            if (jsonResponse) return new(HttpStatusCode.OK) { Content = new StringContent(malformed, Encoding.UTF8, "application/json") };
            var valid = """{"jsonrpc":"2.0","id":IDe0,"result":{"value":"valid"}}""".Replace("ID", id, StringComparison.Ordinal);
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + malformed + "\n\n" +
                (followedByValid ? "data: " + valid + "\n\n" : ""), Encoding.UTF8, "text/event-stream") };
        }
    }
}
