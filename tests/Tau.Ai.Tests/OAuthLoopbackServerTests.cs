// 作者：xxx
using System.Net;
using System.Net.Sockets;
using Tau.Ai.Auth.OAuth;

namespace Tau.Ai.Tests;

/// <summary>【AI】【OAuth 回调测试】通过真实本地 HTTP 请求验证一次性回调的认领、响应与关闭。</summary>
public sealed class OAuthLoopbackServerTests
{
    /// <summary>【AI】【OAuth 回调测试】错误方法、路径、状态或空代码不能消费登录。</summary>
    /// <param name="method">HTTP 方法。</param><param name="path">请求路径。</param><param name="status">预期状态。</param>
    [Theory]
    [InlineData("POST", "/oauth/callback?code=bad&state=expected", 404)]
    [InlineData("GET", "/OAuth/callback?code=bad&state=expected", 404)]
    [InlineData("GET", "/oauth/callback?code=bad&state=wrong", 400)]
    [InlineData("GET", "/oauth/callback?code=bad", 400)]
    [InlineData("GET", "/oauth/callback?state=expected", 400)]
    public async Task InvalidRequests_LeaveLoginPending(string method, string path, int status)
    {
        await using var server = Create((code, _) => Task.FromResult(code), state: "expected");
        using var client = new HttpClient();
        using var invalid = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), new Uri(new Uri(server.RedirectUri), path)));
        Assert.Equal(status, (int)invalid.StatusCode);
        Assert.False(server.WaitAsync().IsCompleted);
        using var valid = await client.GetAsync(server.RedirectUri + "?code=good%2Bvalue&state=expected");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal("good+value", await server.WaitAsync());
        Assert.True(valid.Headers.CacheControl!.NoStore);
    }

    /// <summary>【AI】【OAuth 回调测试】兑换期间不显示成功，重复回调被拒绝，手工取消不能抢占。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ClaimedCallback_WaitsForExchangeAndRejectsDuplicate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchange = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var server = Create((_, _) => { Interlocked.Increment(ref calls); entered.TrySetResult(); return exchange.Task; });
        using var client = new HttpClient();
        var first = client.GetAsync(server.RedirectUri + "?code=first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        server.Cancel();
        Assert.False(server.WaitAsync().IsCompleted);
        Assert.False(first.IsCompleted);
        using var repeated = await client.GetAsync(server.RedirectUri + "?code=second");
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        exchange.SetResult("done");
        using var success = await first;
        Assert.Contains("Signed in to Example.", await success.Content.ReadAsStringAsync());
        Assert.Equal("done", await server.WaitAsync());
        Assert.Equal(1, calls);
    }

    /// <summary>【AI】【OAuth 回调测试】兑换异常返回失败页面并转义错误文本。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ExchangeFailure_Returns502AndSameException()
    {
        var failure = new InvalidOperationException("<script>bad</script>");
        await using var server = Create((_, _) => Task.FromException<string>(failure));
        using var client = new HttpClient();
        using var response = await client.GetAsync(server.RedirectUri + "?code=bad");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("&lt;script&gt;", page);
        Assert.DoesNotContain("<script>", page);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => server.WaitAsync()));
    }

    /// <summary>【AI】【OAuth 回调测试】拒绝授权直接结束等待，不调用兑换函数。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task AuthorizationError_IsTerminal()
    {
        await using var server = Create((_, _) => throw new Xunit.Sdk.XunitException("Unexpected exchange"));
        using var client = new HttpClient();
        using var response = await client.GetAsync(server.RedirectUri + "?error=denied&error_description=User+declined");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Example authorization failed: User declined", (await Assert.ThrowsAsync<InvalidOperationException>(() => server.WaitAsync())).Message);
    }

    /// <summary>【AI】【OAuth 回调测试】手动输入接管后浏览器不能再次兑换。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ManualCancellation_RejectsLaterCallback()
    {
        await using var server = Create((_, _) => throw new Xunit.Sdk.XunitException("Unexpected exchange"));
        server.Cancel();
        Assert.Null(await server.WaitAsync());
        using var client = new HttpClient();
        using var response = await client.GetAsync(server.RedirectUri + "?code=late");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>【AI】【OAuth 回调测试】登录期限独立于 HTTP 请求，超时有提供方名称。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Timeout_RejectsPendingWait()
    {
        await using var server = Create((code, _) => Task.FromResult(code), timeout: TimeSpan.FromMilliseconds(30));
        Assert.Equal("Example sign-in timed out", (await Assert.ThrowsAsync<TimeoutException>(() => server.WaitAsync())).Message);
    }

    /// <summary>【AI】【OAuth 回调测试】取消登录终止等待并保留原取消信号。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Cancellation_RejectsPendingWait()
    {
        using var source = new CancellationTokenSource();
        await using var server = Create((code, _) => Task.FromResult(code), token: source.Token);
        source.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => server.WaitAsync());
        Assert.Equal(source.Token, error.CancellationToken);
    }

    /// <summary>【AI】【OAuth 回调测试】关闭取消慢连接并释放端口，重复关闭可等待。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Dispose_ClosesIncompleteConnectionsAndReleasesPort()
    {
        var server = Create((code, _) => Task.FromResult(code));
        var port = new Uri(server.RedirectUri).Port;
        using var slow = new TcpClient();
        await slow.ConnectAsync(IPAddress.Loopback, port);
        await slow.GetStream().WriteAsync("GET /oauth"u8.ToArray());
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await server.DisposeAsync();
        Assert.Equal("OAuth callback server closed", (await Assert.ThrowsAsync<InvalidOperationException>(() => server.WaitAsync())).Message);
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
    }

    /// <summary>【AI】【OAuth 回调测试】绑定随机端口并用可选的外部主机构造重定向 URL。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RedirectHost_DoesNotChangeListener()
    {
        await using var server = OAuthLoopbackServer<string>.Listen("Example", "127.0.0.1", 0, "/oauth/callback", (code, _) => Task.FromResult(code), default, TimeSpan.FromSeconds(5), redirectHost: "localhost");
        Assert.Equal("localhost", new Uri(server.RedirectUri).Host);
        Assert.NotEqual(0, new Uri(server.RedirectUri).Port);
        using var client = new HttpClient();
        using var response = await client.GetAsync(server.RedirectUri.Replace("localhost", "127.0.0.1", StringComparison.Ordinal) + "?code=ok");
        Assert.Equal("ok", await server.WaitAsync());
    }

    /// <summary>【AI】【OAuth 回调测试】创建仅限本地的隔离服务器。</summary>
    /// <param name="complete">兑换实现。</param><param name="state">状态。</param><param name="token">取消信号。</param><param name="timeout">期限。</param><returns>服务器。</returns>
    private static OAuthLoopbackServer<string> Create(Func<string, CancellationToken, Task<string>> complete, string? state = null, CancellationToken token = default, TimeSpan? timeout = null) =>
        OAuthLoopbackServer<string>.Listen("Example", "127.0.0.1", 0, "/oauth/callback", complete, token, timeout ?? TimeSpan.FromSeconds(10), state);
}
