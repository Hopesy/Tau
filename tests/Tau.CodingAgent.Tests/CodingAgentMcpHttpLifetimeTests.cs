// 作者：xxx
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpHttpLifetimeTests
{
    /// <summary>【MCP】【HTTP 关闭屏障】发送、响应正文和后台事件流全部退出后才释放认证对象，重复关闭等待同一次清理。</summary>
    /// <param name="phase">阻塞发生在响应头、JSON 正文或 SSE 事件流。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("headers")]
    [InlineData("body")]
    [InlineData("events")]
    public async Task McpHttpCloseDrainsRequestsBeforeOwnedAuthenticationAndJoinsRepeatedClose(string phase)
    {
        var blocked = new BlockingRead(); var auth = new OwnedAuthentication(); var deletes = 0; var messages = 0; var closed = 0;
        var stream = new BlockingStream(blocked);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                Assert.True(blocked.Finished.Task.IsCompleted);
                Assert.False(auth.Disposed);
                Interlocked.Increment(ref deletes); return new(HttpStatusCode.NoContent);
            }
            if (phase == "headers") await blocked.WaitAsync(token);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Headers.Add("Mcp-Session-Id", "fixture");
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(phase == "events" ? "text/event-stream" : "application/json");
            return response;
        }));
        await using var transport = new CodingAgentMcpHttpTransport(new("https://fixture.test/mcp"), http, auth: auth, ownsAuth: true, openGetStream: false);
        transport.Message += _ => Interlocked.Increment(ref messages); transport.Closed += () => Interlocked.Increment(ref closed);
        await transport.StartAsync();
        using var document = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"fixture"}""");
        var pending = transport.SendAsync(document.RootElement);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = transport.DisposeAsync().AsTask(); var second = transport.DisposeAsync().AsTask();
        try
        {
            await blocked.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
            Assert.False(auth.Disposed); Assert.Equal(0, closed);
        }
        finally { blocked.Release.TrySetResult(); await Task.WhenAll(first, second); }
        if (phase == "events") await pending;
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(auth.Disposed); Assert.Equal(1, auth.Disposals); Assert.Equal(1, closed); Assert.Equal(0, messages);
        if (phase != "headers") Assert.True(stream.Disposed);
        Assert.Equal(phase == "headers" ? 0 : 1, deletes);
        await Assert.ThrowsAsync<IOException>(() => transport.SendAsync(document.RootElement));
        await transport.DisposeAsync(); Assert.Equal(1, auth.Disposals);
    }

    /// <summary>【MCP】【异常关闭回收】取消监听器或认证释放失败时仍排空发送、通知关闭，并向重复调用方保留同一失败。</summary>
    /// <param name="cancelFails">取消监听器是否抛出异常。</param><param name="disposeFails">认证释放是否抛出异常。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task McpHttpCloseCompletesCleanupDespiteCancellationOrAuthenticationFailure(bool cancelFails, bool disposeFails)
    {
        var blocked = new BlockingRead();
        var auth = new OwnedAuthentication { FailOnCancel = cancelFails, FailOnDispose = disposeFails };
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            await blocked.WaitAsync(token);
            return new(HttpStatusCode.NoContent);
        }));
        var transport = new CodingAgentMcpHttpTransport(new("https://fixture.test/mcp"), http, auth: auth, ownsAuth: true);
        var closed = 0;
        transport.Closed += () =>
        {
            Assert.True(auth.Disposed);
            Assert.True(blocked.Finished.Task.IsCompleted);
            Interlocked.Increment(ref closed);
        };
        await transport.StartAsync();
        using var document = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"fixture"}""");
        var pending = transport.SendAsync(document.RootElement);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = transport.DisposeAsync().AsTask();
        Assert.Same(first, transport.DisposeAsync().AsTask());
        try { await blocked.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { blocked.Release.TrySetResult(); }
        var failure = await Record.ExceptionAsync(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(failure);
        Assert.IsNotType<TimeoutException>(failure);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(auth.Disposed); Assert.Equal(1, auth.Disposals); Assert.Equal(1, closed);
        if (cancelFails) Assert.Contains("fixture cancellation failure", failure.ToString());
        if (disposeFails) Assert.Contains("fixture authentication disposal failure", failure.ToString());
        Assert.Same(first, transport.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<IOException>(() => transport.SendAsync(document.RootElement));
    }

    /// <summary>【MCP】【阻塞读取】模拟收到取消后仍需短暂异步清理的网络操作。</summary>
    private sealed class BlockingRead
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>【MCP】【读取等待】收到取消后等待测试允许清理结束。</summary><param name="token">传输取消信号。</param><returns>读取任务。</returns>
        internal async Task WaitAsync(CancellationToken token)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Cancelled.TrySetResult(); await Release.Task; Finished.TrySetResult(); }
        }
    }

    /// <summary>【MCP】【正文阻塞】通过真实 StreamContent 路径观察正文读取与释放。</summary><param name="blocked">读取控制器。</param>
    private sealed class BlockingStream(BlockingRead blocked) : MemoryStream
    {
        internal bool Disposed { get; private set; }
        /// <summary>【MCP】【正文读取】将异步读取交给取消控制器。</summary><param name="buffer">目标缓存。</param><param name="cancellationToken">取消。</param><returns>读取字节数。</returns>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await blocked.WaitAsync(cancellationToken); return 0; }
        /// <summary>【MCP】【正文释放】记录底层流是否已释放。</summary><param name="disposing">是否释放托管资源。</param>
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    /// <summary>【MCP】【认证生命周期夹具】观察传输拥有的认证对象是否被提前释放。</summary>
    private sealed class OwnedAuthentication : ICodingAgentMcpHttpAuth, IAsyncDisposable
    {
        internal bool Disposed { get; private set; } internal int Disposals { get; private set; }
        internal bool FailOnCancel { get; init; }
        internal bool FailOnDispose { get; init; }
        private CancellationTokenRegistration _registration;
        /// <summary>【MCP】【测试令牌】返回固定测试令牌。</summary><param name="token">取消。</param><returns>令牌任务。</returns>
        public Task<string?> GetTokenAsync(CancellationToken token)
        {
            Assert.False(Disposed);
            if (FailOnCancel) _registration = token.Register(() => throw new IOException("fixture cancellation failure"));
            return Task.FromResult<string?>("fixture");
        }
        /// <summary>【MCP】【测试质询】夹具不执行认证刷新。</summary><param name="response">响应。</param><param name="usedToken">旧令牌。</param><param name="token">取消。</param><returns>完成任务。</returns>
        public Task OnUnauthorizedAsync(HttpResponseMessage response, string? usedToken, CancellationToken token) => Task.CompletedTask;
        /// <summary>【MCP】【认证释放】记录实际释放次数。</summary><returns>完成任务。</returns>
        public ValueTask DisposeAsync()
        {
            Disposals++; Disposed = true; _registration.Dispose();
            return FailOnDispose ? ValueTask.FromException(new IOException("fixture authentication disposal failure")) : ValueTask.CompletedTask;
        }
    }

    /// <summary>【MCP】【HTTP 生命周期夹具】注入受控响应且不访问网络。</summary><param name="send">请求处理器。</param>
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>【MCP】【模拟发送】调用测试定义的请求处理器。</summary><param name="request">请求。</param><param name="cancellationToken">取消。</param><returns>响应。</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
