// 作者：xxx
using System.Net;
using System.Net.Sockets;
using System.Text;
using Tau.CodingAgent.Runtime.Mcp.OAuth;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpOAuthCallbackTests
{
    /// <summary>【CodingAgent】【OAuth 真实回环】未知路径和错误 state 不消费合法等待，成功回调传回发行方且禁止重放。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthCallbackValidatesStateAndRejectsReplay()
    {
        await using var server = CodingAgentMcpOAuthCallbackServer.Listen();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var wait = server.WaitAsync("fixture-state", "/callback");
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.WaitAsync("fixture-state"));
        using var wrongPath = await http.GetAsync(new Uri(new Uri(server.RedirectUrl), "/other?state=fixture-state&code=x"));
        Assert.Equal(HttpStatusCode.NotFound, wrongPath.StatusCode); Assert.False(wait.IsCompleted);
        using var wrongState = await http.GetAsync(server.RedirectUrl + "?state=wrong&code=x");
        Assert.Equal(HttpStatusCode.BadRequest, wrongState.StatusCode); Assert.False(wait.IsCompleted);
        using var success = await http.GetAsync(server.RedirectUrl + "?state=fixture-state&code=fixture%20code&iss=https%3A%2F%2Fissuer.test");
        Assert.Equal(HttpStatusCode.OK, success.StatusCode); Assert.True(success.Headers.CacheControl!.NoStore);
        var result = await wait; Assert.Equal("fixture code", result.Code); Assert.Equal("https://issuer.test", result.Issuer);
        using var replay = await http.GetAsync(server.RedirectUrl + "?state=fixture-state&code=x");
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    /// <summary>【CodingAgent】【OAuth 路径隔离】已知但不匹配的服务器路径拒绝授权；错误或缺少 code 会结束该次等待。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthCallbackBindsServerSpecificPathAndReportsErrors()
    {
        await using var server = CodingAgentMcpOAuthCallbackServer.Listen(new() { ExtraPaths = ["/callback/server"] });
        using var http = new HttpClient();
        var wrong = server.WaitAsync("wrong-path", "/callback/server");
        using var response = await http.GetAsync(server.RedirectUrl + "?state=wrong-path&code=fixture");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("another redirect URI", (await Assert.ThrowsAsync<InvalidOperationException>(() => wrong)).Message);
        var failed = server.WaitAsync("failed");
        using var error = await http.GetAsync(server.RedirectUrl + "?state=failed&error=access_denied&error_description=fixture+denied");
        Assert.Equal(HttpStatusCode.OK, error.StatusCode);
        Assert.Equal("fixture denied", (await Assert.ThrowsAsync<InvalidOperationException>(() => failed)).Message);
        var missing = server.WaitAsync("missing");
        using var empty = await http.GetAsync(server.RedirectUrl + "?state=missing");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing);
    }

    /// <summary>【CodingAgent】【OAuth 回环生命周期】超时和取消清除 state，关闭排空慢请求并立即释放端口。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthCallbackClosesSlowConnectionsAndReleasesPort()
    {
        var server = CodingAgentMcpOAuthCallbackServer.Listen(new() { Timeout = TimeSpan.FromMilliseconds(120) });
        await using var cleanup = server;
        var uri = new Uri(server.RedirectUrl);
        await Assert.ThrowsAsync<TimeoutException>(() => server.WaitAsync("timeout"));
        using var cancellation = new CancellationTokenSource();
        var cancelled = server.WaitAsync("cancelled", token: cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, uri.Port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /callback HTTP/1.1\r\nHost: localhost\r\n"));
        var pending = server.WaitAsync("close");
        var closeOne = server.DisposeAsync().AsTask(); var closeTwo = server.DisposeAsync().AsTask();
        await Task.WhenAll(closeOne, closeTwo).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        await using var reused = CodingAgentMcpOAuthCallbackServer.Listen(new() { Port = uri.Port });
        await Assert.ThrowsAsync<ObjectDisposedException>(() => server.WaitAsync("after-close"));
    }

    /// <summary>【CodingAgent】【OAuth 手工回调】完整 URI、端口、路径、state 和授权码均需匹配，不接受裸授权码。</summary>
    [Fact]
    public void McpOAuthManualRedirectValidatesFullIdentity()
    {
        var redirect = new Uri("http://127.0.0.1:3200/callback/server");
        var result = CodingAgentMcpOAuthSignIn.ParseRedirect(redirect + "?state=fixture&code=one&code=two&iss=https%3A%2F%2Fissuer.test", "fixture", redirect);
        Assert.Equal("one", result.Code); Assert.Equal("https://issuer.test", result.Issuer);
        foreach (var input in new[] { "bare-code", "http://127.0.0.1:3201/callback/server?state=fixture&code=x", "http://127.0.0.1:3200/callback/other?state=fixture&code=x", redirect + "?state=wrong&code=x", redirect + "?state=fixture" })
            Assert.Throws<InvalidDataException>(() => CodingAgentMcpOAuthSignIn.ParseRedirect(input, "fixture", redirect));
        Assert.Equal("fixture denied", Assert.Throws<InvalidOperationException>(() =>
            CodingAgentMcpOAuthSignIn.ParseRedirect(redirect + "?state=fixture&error=denied&error_description=fixture+denied", "fixture", redirect)).Message);
    }
}
