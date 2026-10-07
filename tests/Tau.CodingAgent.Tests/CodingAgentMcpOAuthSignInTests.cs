// 作者：xxx
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;
using Tau.CodingAgent.Runtime.Mcp.OAuth;
using static Tau.CodingAgent.Tests.CodingAgentMcpOAuthProtocolTests;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpOAuthSignInTests
{
    /// <summary>【CodingAgent】【OAuth 交互登录】浏览器与手工输入均可完成 PKCE，权限升级合并配置及既有权限并生成新 state。</summary>
    /// <param name="browser">是否走真实回环请求。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task McpOAuthSignInCompletesBrowserAndManualFlows(bool browser)
    {
        var store = new CodingAgentMcpOAuthMemoryStore();
        var provider = await CodingAgentMcpOAuthFlowTests.CachedProviderAsync(new() { ServerUrl = "https://mcp.test/", RedirectUrl = "http://127.0.0.1/callback", Store = store });
        await provider.SaveTokensAsync(new() { ["access_token"] = "fixture-old", ["token_type"] = "Bearer", ["refresh_token"] = "fixture-refresh", ["scope"] = "read" });
        var oldState = await provider.StateAsync(); var requests = 0; Uri? redirect = null; string? callback = null; var inputCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            requests++; var form = Form(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("authorization_code", form["grant_type"]); Assert.Equal("fixture-code", form["code"]);
            Assert.Equal(callback!.Split('?', 2)[0], form["redirect_uri"]);
            return Response(200, """{"access_token":"fixture-login","token_type":"Bearer","refresh_token":"fixture-refresh-new"}""");
        }));
        var prompt = new Prompt(async (url, token) =>
        {
            var query = Form(url.Query); Assert.Equal("write read admin", query["scope"]); Assert.NotEqual(oldState, query["state"]);
            redirect = new(query["redirect_uri"]); callback = redirect + "?code=fixture-code&state=" + query["state"];
            if (browser)
            {
                using var browserHttp = new HttpClient();
                using var response = await browserHttp.GetAsync(callback, token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("Signed in", await response.Content.ReadAsStringAsync(token));
            }
        }, async token =>
        {
            if (!browser) return callback;
            try { await Task.Delay(Timeout.Infinite, token); return null; }
            finally { inputCancelled.TrySetResult(); }
        });
        await CodingAgentMcpOAuthSignIn.SignInAsync("https://mcp.test/", store, new() { ClientId = "fixture", Scope = "write" }, prompt,
            new(Scope: "admin", Error: "insufficient_scope"), http);
        Assert.Equal(1, requests); Assert.Equal("write read admin", (await provider.TokensAsync())!["scope"]!.GetValue<string>());
        if (browser) await inputCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var released = CodingAgentMcpOAuthCallbackServer.Listen(new() { Port = redirect!.Port });
    }

    /// <summary>【CodingAgent】【OAuth 旧端口迁移】注册端口占用时选择新端口并重新注册客户端，固定端口占用则明确失败。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthSignInReRegistersWhenOldPortIsBusyAndPreservesFixedPortFailure()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var store = new CodingAgentMcpOAuthMemoryStore();
        var provider = await CodingAgentMcpOAuthFlowTests.CachedProviderAsync(new() { ServerUrl = "https://mcp.test/", RedirectUrl = "http://127.0.0.1/callback", Store = store });
        await provider.SaveClientInformationAsync(new() { ["client_id"] = "fixture-old", ["redirect_uris"] = new JsonArray($"http://127.0.0.1:{port}/callback") });
        await provider.SaveTokensAsync(new() { ["access_token"] = "fixture-old", ["token_type"] = "Bearer" });
        var registrations = 0; string? callback = null;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/register")
            {
                registrations++; var metadata = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                Assert.NotEqual(port, new Uri(metadata["redirect_uris"]![0]!.GetValue<string>()).Port);
                return Response(201, new JsonObject { ["client_id"] = "fixture-new", ["redirect_uris"] = metadata["redirect_uris"]!.DeepClone() }.ToJsonString());
            }
            Assert.Equal("fixture-new", Form(await request.Content!.ReadAsStringAsync(token))["client_id"]);
            return Response(200, """{"access_token":"fixture-login","token_type":"Bearer"}""");
        }));
        var prompt = new Prompt((url, _) => { var query = Form(url.Query); callback = query["redirect_uri"] + "?code=fixture&state=" + query["state"]; return Task.CompletedTask; }, _ => Task.FromResult(callback));
        await Assert.ThrowsAsync<SocketException>(() => CodingAgentMcpOAuthSignIn.SignInAsync("https://mcp.test/", store, new() { CallbackPort = port }, prompt, httpClient: http));
        Assert.Equal("fixture-old", (await provider.TokensAsync())!["access_token"]!.GetValue<string>());
        await CodingAgentMcpOAuthSignIn.SignInAsync("https://mcp.test/", store, new(), prompt, httpClient: http);
        Assert.Equal(1, registrations); Assert.Equal("fixture-login", (await provider.TokensAsync())!["access_token"]!.GetValue<string>());
    }

    /// <summary>【CodingAgent】【OAuth 取消登录】空输入或取消展示均关闭回环服务，不发送授权码交换。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthSignInCancellationClosesCallbackWithoutExchangingCode()
    {
        var store = new CodingAgentMcpOAuthMemoryStore();
        await CodingAgentMcpOAuthFlowTests.CachedProviderAsync(new() { ServerUrl = "https://mcp.test/", RedirectUrl = "http://127.0.0.1/callback", Store = store });
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP")));
        Uri? redirect = null;
        var prompt = new Prompt((url, _) => { redirect = new(Form(url.Query)["redirect_uri"]); return Task.CompletedTask; }, _ => Task.FromResult<string?>(""));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodingAgentMcpOAuthSignIn.SignInAsync("https://mcp.test/", store, new() { ClientId = "fixture" }, prompt, httpClient: http));
        await using var released = CodingAgentMcpOAuthCallbackServer.Listen(new() { Port = redirect!.Port });
        Assert.Null((await store.LoadAsync())!["tokens"]);
    }

    /// <summary>【CodingAgent】【MCP 服务登录注销】连接状态从待登录到可用再到待登录，登录后刷新工具目录，注销只删除本服务器账号。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthServiceSignInAndSignOutReconnectWithoutTouchingOtherAccounts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-mcp-oauth-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var credentials = new CodingAgentMcpOAuthCredentialStore(directory);
            await credentials.ForServer("other", "https://mcp.test/").SaveAsync(new() { ["serverUrl"] = "https://mcp.test/", ["tokens"] = new JsonObject { ["access_token"] = "fixture-other" } });
            using var http = ServiceHttp();
            var registry = new CodingAgentMcpServerRegistry();
            registry.Register("fixture", new JsonObject { ["url"] = "https://mcp.test/", ["oauth"] = new JsonObject { ["clientId"] = "fixture" } }, "fixture");
            await using var service = new CodingAgentMcpService(directory, directory, () => true, registry, new() { HttpClient = http });
            await service.ReloadAsync(); await service.WaitForServersAsync(); Assert.Equal("needs-auth", Assert.Single(service.GetStatus()).State);
            string? callback = null;
            var prompt = new Prompt((url, _) => { var query = Form(url.Query); callback = query["redirect_uri"] + "?state=" + query["state"] + "&code=fixture"; return Task.CompletedTask; }, _ => Task.FromResult(callback));
            await service.SignInAsync("fixture", prompt);
            Assert.Equal("connected", Assert.Single(service.GetStatus()).State); Assert.Single(service.GetTools());
            Assert.Equal("fixture-login", (await credentials.TokensAsync("fixture", "https://mcp.test/"))!["access_token"]!.GetValue<string>());
            await service.SignOutAsync("fixture");
            Assert.Equal("needs-auth", Assert.Single(service.GetStatus()).State); Assert.Null(await credentials.TokensAsync("fixture", "https://mcp.test/"));
            Assert.Equal("fixture-other", (await credentials.TokensAsync("other", "https://mcp.test/"))!["access_token"]!.GetValue<string>());
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>【CodingAgent】【MCP 关闭登录】会话关闭取消进行中的手工输入并等待回环服务释放，重复关闭共享完成。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthServiceShutdownCancelsSignInAndDrainsCallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-mcp-oauth-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var http = ServiceHttp();
            var registry = new CodingAgentMcpServerRegistry();
            registry.Register("fixture", new JsonObject { ["url"] = "https://mcp.test/", ["oauth"] = new JsonObject { ["clientId"] = "fixture" } }, "fixture");
            await using var service = new CodingAgentMcpService(directory, directory, () => true, registry, new() { HttpClient = http });
            await service.ReloadAsync(); await service.WaitForServersAsync();
            var started = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompt = new Prompt((url, _) => { started.TrySetResult(new(Form(url.Query)["redirect_uri"])); return Task.CompletedTask; },
                async token => { await Task.Delay(Timeout.Infinite, token); return null; });
            var signIn = service.SignInAsync("fixture", prompt); var callback = await started.Task;
            var first = service.DisposeAsync().AsTask(); var second = service.DisposeAsync().AsTask();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
            await using var released = CodingAgentMcpOAuthCallbackServer.Listen(new() { Port = callback.Port });
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>【CodingAgent】【OAuth 服务模拟】同时提供资源发现、授权端点和带认证的 MCP 工具。</summary><returns>调用方管理的 HTTP 客户端。</returns>
    internal static HttpClient ServiceHttp() => new(new Handler(async (request, token) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/.well-known/oauth-protected-resource") return Response(200, """{"resource":"https://mcp.test/","authorization_servers":["https://issuer.test"]}""");
        if (path == "/.well-known/oauth-authorization-server") return Response(200, Metadata().ToJsonString());
        if (path == "/token") return Response(200, """{"access_token":"fixture-login","token_type":"Bearer"}""");
        if (request.Method == HttpMethod.Get) return Response(405, "");
        if (request.Method == HttpMethod.Delete) return Response(204, "");
        if (request.Headers.Authorization?.Parameter != "fixture-login") return Response(401, "");
        var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
        if (!message.ContainsKey("id")) return Response(202, "");
        var result = message["method"]!.GetValue<string>() == "initialize"
            ? JsonNode.Parse("""{"protocolVersion":"2025-11-25","capabilities":{"tools":{}},"serverInfo":{"name":"fixture","version":"1"}}""")
            : JsonNode.Parse("""{"tools":[{"name":"echo","inputSchema":{"type":"object"}}]}""");
        return Response(200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
    }));

    /// <summary>【CodingAgent】【OAuth 测试交互】通过委托模拟浏览器和手工输入，不打开真实浏览器。</summary><param name="show">展示回调。</param><param name="input">输入回调。</param>
    internal sealed class Prompt(Func<Uri, CancellationToken, Task> show, Func<CancellationToken, Task<string?>> input) : ICodingAgentMcpSignInPrompt
    {
        /// <summary>【CodingAgent】【测试授权展示】执行测试展示。</summary><param name="url">授权 URL。</param><param name="token">取消。</param><returns>展示任务。</returns>
        public Task ShowAuthorizationUrlAsync(Uri url, CancellationToken token) => show(url, token);
        /// <summary>【CodingAgent】【测试回调输入】执行测试输入。</summary><param name="token">取消。</param><returns>回调文本。</returns>
        public Task<string?> PromptForRedirectUrlAsync(CancellationToken token) => input(token);
    }
}
