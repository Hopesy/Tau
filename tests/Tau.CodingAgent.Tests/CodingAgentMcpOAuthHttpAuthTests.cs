// 作者：xxx
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;
using Tau.CodingAgent.Runtime.Mcp.OAuth;
using static Tau.CodingAgent.Tests.CodingAgentMcpOAuthProtocolTests;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpOAuthHttpAuthTests
{
    /// <summary>【CodingAgent】【OAuth 并发轮换】多个处理器共享服务器锁，过期读取和并发 401 只轮换一次。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthHttpAuthSharesRefreshAcrossProvidersAndIgnoresStaleUnauthorized()
    {
        var store = new CodingAgentMcpOAuthMemoryStore(); await SeedAsync(store, expires: 0);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var settings = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Interlocked.Increment(ref calls); started.TrySetResult(); await release.Task.WaitAsync(token);
            Assert.Equal("fixture-refresh", Form(await request.Content!.ReadAsStringAsync(token))["refresh_token"]);
            return Response(200, """{"access_token":"fixture-new","token_type":"Bearer","refresh_token":"fixture-rotated","expires_in":3600}""");
        }));
        /// <summary>【CodingAgent】【设置解析计数】观察实际触发刷新时才读取设置。</summary><param name="_">取消。</param><returns>测试设置。</returns>
        Task<CodingAgentMcpOAuthSettings> Settings(CancellationToken _) { Interlocked.Increment(ref settings); return Task.FromResult(new CodingAgentMcpOAuthSettings { ClientId = "fixture" }); }
        await using var one = new CodingAgentMcpOAuthHttpAuth("https://mcp.test/", store, Settings, httpClient: http);
        await using var two = new CodingAgentMcpOAuthHttpAuth("https://mcp.test/", store, Settings, httpClient: http);
        var first = one.GetTokenAsync(CancellationToken.None); await started.Task;
        using var unauthorized = Response(401, "");
        var waiting = Enumerable.Range(0, 10).Select(index => (index % 2 == 0 ? one : two).OnUnauthorizedAsync(unauthorized, "fixture-old", CancellationToken.None)).ToArray();
        release.SetResult(); await Task.WhenAll(waiting); Assert.Equal("fixture-new", await first);
        Assert.Equal(1, calls); Assert.Equal(1, settings);
        await two.OnUnauthorizedAsync(unauthorized, "fixture-old", CancellationToken.None);
        Assert.Equal(1, calls); Assert.Equal("fixture-rotated", (await store.LoadAsync())!["tokens"]!["refresh_token"]!.GetValue<string>());
    }

    /// <summary>【CodingAgent】【OAuth 取消保存】调用方取消只结束自己的等待，关闭须等轮换令牌提交后才结束。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthHttpAuthCancellationDoesNotLoseRotatedCredentials()
    {
        var store = new CodingAgentMcpOAuthMemoryStore(); await SeedAsync(store);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            started.SetResult(); await release.Task.WaitAsync(token);
            return Response(200, """{"access_token":"fixture-new","token_type":"Bearer","refresh_token":"fixture-rotated"}""");
        }));
        await using var auth = new CodingAgentMcpOAuthHttpAuth("https://mcp.test/", store, _ => Task.FromResult(new CodingAgentMcpOAuthSettings { ClientId = "fixture" }), httpClient: http);
        using var cancellation = new CancellationTokenSource(); using var unauthorized = Response(401, "");
        var request = auth.OnUnauthorizedAsync(unauthorized, "fixture-old", cancellation.Token);
        await started.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var close = auth.DisposeAsync().AsTask(); Assert.False(close.IsCompleted);
        release.SetResult(); await close; await auth.DisposeAsync();
        Assert.Equal("fixture-rotated", (await store.LoadAsync())!["tokens"]!["refresh_token"]!.GetValue<string>());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => auth.GetTokenAsync(CancellationToken.None));
    }

    /// <summary>【CodingAgent】【OAuth 登录边界】无刷新令牌和权限升级不解析密钥、不访问网络，质询保留给显式登录。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthHttpAuthRequiresExplicitLoginForMissingGrantOrAdditionalScope()
    {
        var store = new CodingAgentMcpOAuthMemoryStore(); CodingAgentMcpOAuthChallenge? challenge = null;
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected network")));
        await using var auth = new CodingAgentMcpOAuthHttpAuth("https://mcp.test/", store, _ => throw new InvalidOperationException("Unexpected settings"), value => challenge = value, http);
        Assert.Null(await auth.GetTokenAsync(CancellationToken.None));
        using var missing = Response(401, ""); missing.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer resource_metadata=\"https://mcp.test/meta\", scope=\"read\"");
        await Assert.ThrowsAsync<CodingAgentMcpOAuthAuthorizationRequiredException>(() => auth.OnUnauthorizedAsync(missing, null, CancellationToken.None));
        Assert.Equal("https://mcp.test/meta", challenge!.ResourceMetadataUrl!.AbsoluteUri);
        await SeedAsync(store);
        using var stepUp = Response(403, ""); stepUp.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer error=\"insufficient_scope\", scope=\"admin\"");
        await Assert.ThrowsAsync<CodingAgentMcpOAuthAuthorizationRequiredException>(() => auth.OnUnauthorizedAsync(stepUp, "fixture-old", CancellationToken.None));
        Assert.Equal("admin", challenge!.Scope); Assert.Equal("fixture-old", await auth.GetTokenAsync(CancellationToken.None));
    }

    /// <summary>【CodingAgent】【OAuth 刷新失败】提前刷新失败时保留旧访问令牌，认证响应后明确要求登录，不能把失败结果当成功令牌。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthHttpAuthFallsBackToOldAccessTokenAfterTransientRefreshFailure()
    {
        var store = new CodingAgentMcpOAuthMemoryStore(); await SeedAsync(store, expires: 0);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(503, "fixture outage"))));
        await using var auth = new CodingAgentMcpOAuthHttpAuth("https://mcp.test/", store, _ => Task.FromResult(new CodingAgentMcpOAuthSettings { ClientId = "fixture" }), httpClient: http);
        Assert.Equal("fixture-old", await auth.GetTokenAsync(CancellationToken.None));
        using var unauthorized = Response(401, "");
        await Assert.ThrowsAsync<CodingAgentMcpOAuthAuthorizationRequiredException>(() => auth.OnUnauthorizedAsync(unauthorized, "fixture-old", CancellationToken.None));
        Assert.Equal("fixture-refresh", (await store.LoadAsync())!["tokens"]!["refresh_token"]!.GetValue<string>());
    }

    /// <summary>【CodingAgent】【MCP OAuth 服务接入】服务默认读取持久化令牌，401 后真实传输重发新令牌，权限升级进入待登录状态。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthServiceRefreshesDefaultHttpAuthAndCapturesStepUp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-mcp-oauth-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await SeedAsync(new CodingAgentMcpOAuthCredentialStore(directory).ForServer("fixture", "https://mcp.test/"));
            var refreshes = 0; var oldRequests = 0; var stepUp = false;
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                if (request.RequestUri!.Host == "issuer.test")
                {
                    Interlocked.Increment(ref refreshes);
                    return Response(200, """{"access_token":"fixture-new","token_type":"Bearer","refresh_token":"fixture-rotated"}""");
                }
                if (request.Method == HttpMethod.Get) return Response(405, "");
                if (request.Method == HttpMethod.Delete) return Response(204, "");
                if (request.Headers.Authorization?.Parameter != "fixture-new")
                {
                    Interlocked.Increment(ref oldRequests); var unauthorized = Response(401, "");
                    unauthorized.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer scope=\"read\""); return unauthorized;
                }
                var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
                if (!message.ContainsKey("id")) return Response(202, "");
                if (message["method"]!.GetValue<string>() == "tools/call" && stepUp)
                {
                    var denied = Response(403, ""); denied.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer error=\"insufficient_scope\", scope=\"admin\"");
                    return denied;
                }
                var result = message["method"]!.GetValue<string>() switch
                {
                    "initialize" => JsonNode.Parse("""{"protocolVersion":"2025-11-25","capabilities":{"tools":{}},"serverInfo":{"name":"fixture","version":"1"}}"""),
                    "tools/list" => JsonNode.Parse("""{"tools":[{"name":"echo","description":"fixture","inputSchema":{"type":"object"}}]}"""),
                    _ => JsonNode.Parse("""{"content":[{"type":"text","text":"ok"}]}""")
                };
                return Response(200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
            }));
            var registry = new CodingAgentMcpServerRegistry();
            registry.Register("fixture", new JsonObject { ["url"] = "https://mcp.test/", ["exposure"] = "direct", ["oauth"] = new JsonObject { ["clientId"] = "fixture" } }, "test");
            await using var service = new CodingAgentMcpService(directory, directory, () => true, registry, new() { HttpClient = http });
            await service.ReloadAsync(); await service.WaitForServersAsync();
            Assert.Equal("connected", Assert.Single(service.GetStatus()).State); Assert.Equal(1, refreshes); Assert.Equal(1, oldRequests);
            var tool = Assert.Single(service.GetTools()); stepUp = true;
            using var args = JsonDocument.Parse("{}");
            await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync("fixture", args.RootElement.Clone(), CancellationToken.None));
            Assert.Equal("needs-auth", Assert.Single(service.GetStatus()).State); Assert.Equal("admin", service.GetOAuthChallenge("fixture")!.Scope);
            Assert.Equal(1, refreshes);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>【CodingAgent】【MCP 首次登录状态】没有凭据的服务器只发一次匿名初始化，不产生授权发现或凭据文件。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthServiceDoesNotStartLoginWhenNoCredentialsExist()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-mcp-oauth-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var calls = 0;
            using var http = new HttpClient(new Handler((request, _) =>
            {
                calls++; Assert.Equal("mcp.test", request.RequestUri!.Host); Assert.Null(request.Headers.Authorization);
                return Task.FromResult(Response(401, ""));
            }));
            var registry = new CodingAgentMcpServerRegistry();
            registry.Register("fixture", new JsonObject { ["url"] = "https://mcp.test/", ["oauth"] = new JsonObject { ["clientSecret"] = "!this-command-must-not-run" } }, "test");
            await using var service = new CodingAgentMcpService(directory, directory, () => true, registry, new() { HttpClient = http });
            await service.ReloadAsync(); await service.WaitForServersAsync();
            Assert.Equal(1, calls); Assert.Equal("needs-auth", Assert.Single(service.GetStatus()).State);
            Assert.False(File.Exists(Path.Combine(directory, "mcp-auth.json")));
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>【CodingAgent】【OAuth 测试凭据】写入独立服务器的测试状态，可指定整数过期时间覆盖内存节点边界。</summary>
    /// <param name="store">临时存储。</param><param name="expires">过期时间或无限期。</param><returns>写入任务。</returns>
    private static Task SeedAsync(ICodingAgentMcpOAuthStateStore store, long? expires = null)
    {
        var state = new JsonObject
        {
            ["serverUrl"] = "https://mcp.test/", ["tokens"] = new JsonObject { ["access_token"] = "fixture-old", ["token_type"] = "Bearer", ["refresh_token"] = "fixture-refresh", ["scope"] = "read" },
            ["clientInformation"] = new JsonObject { ["client_id"] = "fixture", ["redirect_uris"] = new JsonArray("http://127.0.0.1/callback") },
            ["discovery"] = new JsonObject { ["authorizationServerUrl"] = "https://issuer.test", ["authorizationServerMetadata"] = Metadata() }
        };
        if (expires is { } value) state["tokensExpireAt"] = value;
        return store.SaveAsync(state);
    }
}
