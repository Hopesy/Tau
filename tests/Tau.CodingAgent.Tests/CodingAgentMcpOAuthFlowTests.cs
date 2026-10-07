// 作者：xxx
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime.Mcp.OAuth;
using static Tau.CodingAgent.Tests.CodingAgentMcpOAuthProtocolTests;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpOAuthFlowTests
{
    /// <summary>【CodingAgent】【OAuth 完整授权】动态注册、PKCE 跳转、发行方校验和授权码交换使用同一客户端与回调。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthAuthorizationCodePreservesPkceScopeResourceAndIssuer()
    {
        var calls = new List<string>(); Dictionary<string, string>? exchanged = null;
        var metadata = Metadata(); metadata["authorization_response_iss_parameter_supported"] = true;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.StartsWith("/.well-known/oauth-protected-resource", StringComparison.Ordinal))
                return Response(200, """{"resource":"https://mcp.test/api","authorization_servers":["https://issuer.test"],"scopes_supported":["read","offline_access"]}""");
            if (request.RequestUri.AbsolutePath == "/.well-known/oauth-authorization-server") return Response(200, metadata.ToJsonString());
            if (request.RequestUri.AbsolutePath == "/register")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                Assert.Equal("read offline_access", body["scope"]!.GetValue<string>());
                Assert.Equal("http://127.0.0.1:3210/callback", body["redirect_uris"]![0]!.GetValue<string>());
                return Response(201, """{"client_id":"fixture-client","token_endpoint_auth_method":"none","redirect_uris":["http://127.0.0.1:3210/callback"]}""");
            }
            Assert.Equal("/token", request.RequestUri.AbsolutePath); Assert.Null(request.Headers.Authorization);
            exchanged = Form(await request.Content!.ReadAsStringAsync(token));
            return Response(200, """{"access_token":"fixture-access","token_type":"Bearer","refresh_token":"fixture-refresh","expires_in":"120"}""");
        }));
        var store = new CodingAgentMcpOAuthMemoryStore(); Uri? authorization = null;
        var provider = new CodingAgentMcpOAuthProvider(new() { ServerUrl = "https://mcp.test/api/mcp", RedirectUrl = "http://127.0.0.1:3210/callback", Store = store,
            OnRedirect = (url, _) => { authorization = url; return Task.CompletedTask; } });
        var flow = new CodingAgentMcpOAuthFlow(http); var options = new CodingAgentMcpOAuthFlowOptions { ServerUrl = new("https://mcp.test/api/mcp") };
        Assert.Equal(CodingAgentMcpOAuthFlowResult.Redirect, await flow.AuthorizeAsync(provider, options));
        var query = Form(authorization!.Query); var verifier = await provider.CodeVerifierAsync();
        Assert.Equal(43, verifier.Length); Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'), query["code_challenge"]);
        Assert.Equal(await provider.StateAsync(), query["state"]); Assert.Equal("yes", query["keep"]);
        Assert.Equal("consent", query["prompt"]); Assert.Equal("https://mcp.test/api", query["resource"]);
        var before = calls.Count;
        await Assert.ThrowsAsync<CodingAgentMcpOAuthIssuerException>(() => flow.AuthorizeAsync(provider, options with { AuthorizationCode = "fixture-code" }));
        await Assert.ThrowsAsync<CodingAgentMcpOAuthIssuerException>(() => flow.AuthorizeAsync(provider, options with { AuthorizationCode = "fixture-code", Issuer = "https://wrong.test" }));
        Assert.Equal(before, calls.Count);
        Assert.Equal(CodingAgentMcpOAuthFlowResult.Authorized, await flow.AuthorizeAsync(provider, options with { AuthorizationCode = "fixture-code", Issuer = "https://issuer.test" }));
        Assert.Equal("authorization_code", exchanged!["grant_type"]); Assert.Equal(verifier, exchanged["code_verifier"]);
        Assert.Equal("fixture-client", exchanged["client_id"]); Assert.Equal(query["redirect_uri"], exchanged["redirect_uri"]);
        Assert.Equal("read offline_access", (await provider.TokensAsync())!["scope"]!.GetValue<string>());
        Assert.NotNull((await store.LoadAsync())!["tokensExpireAt"]); Assert.Equal(before + 1, calls.Count);
    }

    /// <summary>【CodingAgent】【OAuth 客户端认证】基本认证、表单认证及宿主自定义认证按能力选择，刷新保留或轮换令牌。</summary>
    /// <param name="method">客户端认证方式。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("client_secret_basic")]
    [InlineData("client_secret_post")]
    [InlineData("none")]
    [InlineData("custom")]
    public async Task McpOAuthRefreshSelectsAuthenticationAndPreservesRefreshToken(string method)
    {
        var metadata = Metadata(); metadata["token_endpoint_auth_methods_supported"] = new JsonArray(method);
        var count = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            count++; var body = Form(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("refresh_token", body["grant_type"]); Assert.Equal("fixture-old-refresh", body["refresh_token"]); Assert.Equal("https://mcp.test/api", body["resource"]);
            if (method == "client_secret_basic")
            {
                Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
                Assert.Equal("fixture-id:fixture-secret", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
                Assert.False(body.ContainsKey("client_id")); Assert.False(body.ContainsKey("client_secret"));
            }
            else if (method == "custom") { Assert.Equal("fixture-custom", request.Headers.GetValues("X-Client").Single()); Assert.False(body.ContainsKey("client_id")); }
            else
            {
                Assert.Null(request.Headers.Authorization); Assert.Equal("fixture-id", body["client_id"]);
                Assert.Equal(method == "client_secret_post", body.ContainsKey("client_secret"));
            }
            return Response(200, count == 1 ? """{"access_token":"fixture-new","token_type":"Bearer"}""" : """{"access_token":"fixture-next","token_type":"Bearer","refresh_token":"fixture-rotated"}""");
        }));
        var options = new CodingAgentMcpOAuthTokenOptions { Metadata = metadata, ClientInformation = new() { ["client_id"] = "fixture-id", ["client_secret"] = "fixture-secret" }, Resource = "https://mcp.test/api",
            AddClientAuthentication = method == "custom" ? (headers, body, url, server, _) => { Assert.Same(metadata, server); Assert.Equal("https://issuer.test/token", url.AbsoluteUri); headers.Add("X-Client", "fixture-custom"); return Task.CompletedTask; } : null };
        var flow = new CodingAgentMcpOAuthFlow(http);
        Assert.Equal("fixture-old-refresh", (await flow.RefreshAsync(new("https://issuer.test"), options, "fixture-old-refresh"))["refresh_token"]!.GetValue<string>());
        Assert.Equal("fixture-rotated", (await flow.RefreshAsync(new("https://issuer.test"), options, "fixture-old-refresh"))["refresh_token"]!.GetValue<string>());
    }

    /// <summary>【CodingAgent】【OAuth 错误响应】200 中的协议错误优先解析，不安全端点在请求前失败，失效授权只清令牌后跳转一次。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthRejectsInsecureEndpointsAndRecoversInvalidGrant()
    {
        var requests = 0; var redirects = 0;
        using var http = new HttpClient(new Handler((_, _) => { requests++; return Task.FromResult(Response(200, """{"error":"invalid_grant","error_description":"fixture expired","error_uri":"https://issuer.test/help"}""")); }));
        var flow = new CodingAgentMcpOAuthFlow(http);
        var metadata = Metadata(); metadata["token_endpoint"] = "http://public.test/token";
        var options = new CodingAgentMcpOAuthTokenOptions { ClientInformation = new() { ["client_id"] = "fixture" }, Metadata = metadata };
        await Assert.ThrowsAsync<CodingAgentMcpOAuthInsecureEndpointException>(() => flow.RefreshAsync(new("https://issuer.test"), options, "fixture-refresh"));
        Assert.Equal(0, requests); metadata["token_endpoint"] = "http://127.0.0.1:3210/token";
        var error = await Assert.ThrowsAsync<CodingAgentMcpOAuthException>(() => flow.RefreshAsync(new("https://issuer.test"), options, "fixture-refresh"));
        Assert.Equal("invalid_grant", error.Code); Assert.Equal("https://issuer.test/help", error.ErrorUri);
        var provider = await CachedProviderAsync(new() { ClientId = "fixture", ServerUrl = "https://mcp.test/", RedirectUrl = "http://127.0.0.1/callback",
            OnRedirect = (_, _) => { redirects++; return Task.CompletedTask; } });
        await provider.SaveTokensAsync(new() { ["access_token"] = "fixture-old", ["token_type"] = "Bearer", ["refresh_token"] = "fixture-refresh", ["scope"] = "read" });
        Assert.Equal(CodingAgentMcpOAuthFlowResult.Redirect, await flow.AuthorizeAsync(provider, new() { ServerUrl = new("https://mcp.test/") }));
        Assert.Equal(2, requests); Assert.Equal(1, redirects); Assert.Null(await provider.TokensAsync()); Assert.NotNull(await provider.DiscoveryStateAsync());
    }

    /// <summary>【CodingAgent】【OAuth 刷新权限】普通刷新继承旧 scope，权限升级跳过刷新并保留旧权限；CIMD 不写入动态注册。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthRefreshAndCimdUseGrantScopeAndDocumentRedirect()
    {
        var calls = 0; Uri? redirect = null;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Response(200, """{"access_token":"fixture-new","token_type":"Bearer"}""")); }));
        var provider = await CachedProviderAsync(new() { ServerUrl = "https://mcp.test/", RedirectUrl = "http://127.0.0.1/callback",
            ClientMetadataDocument = _ => new("https://client.test/client.json", "http://127.0.0.1/callback/server"),
            OnRedirect = (url, _) => { redirect = url; return Task.CompletedTask; } });
        await provider.SaveTokensAsync(new() { ["access_token"] = "fixture-old", ["token_type"] = "Bearer", ["refresh_token"] = "fixture-refresh", ["scope"] = "read" });
        var flow = new CodingAgentMcpOAuthFlow(http); var options = new CodingAgentMcpOAuthFlowOptions { ServerUrl = new("https://mcp.test/"), Scope = "unused" };
        Assert.Equal(CodingAgentMcpOAuthFlowResult.Authorized, await flow.AuthorizeAsync(provider, options));
        Assert.Equal("read", (await provider.TokensAsync())!["scope"]!.GetValue<string>());
        Assert.Equal(CodingAgentMcpOAuthFlowResult.Redirect, await flow.AuthorizeAsync(provider, options with { SkipRefresh = true, Scope = CodingAgentMcpOAuthFlow.StepUpScope("read", "admin") }));
        Assert.Equal(1, calls); Assert.Null(await provider.ClientInformationAsync());
        Assert.Equal("http://127.0.0.1/callback/server", Form(redirect!.Query)["redirect_uri"]);
        Assert.Equal("read admin", Form(redirect.Query)["scope"]);
        Assert.Equal("https://client.test/client.json", Form(redirect.Query)["client_id"]);
    }

    /// <summary>【CodingAgent】【OAuth 显式发现】授权元数据覆盖不复用旧缓存，不匹配的文档 issuer 按配置可信处理。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthExplicitMetadataOverridesCachedIssuerWithoutReplacingCache()
    {
        var endpoints = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            endpoints.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath.StartsWith("/.well-known/", StringComparison.Ordinal)) return Task.FromResult(Response(404, ""));
            var metadata = Metadata("https://new.test"); metadata["authorization_endpoint"] = "https://new.test/authorize";
            return Task.FromResult(Response(200, metadata.ToJsonString()));
        }));
        Uri? redirect = null;
        var provider = await CachedProviderAsync(new() { ServerUrl = "https://mcp.test/", RedirectUrl = "http://127.0.0.1/callback", ClientId = "fixture",
            OnRedirect = (url, _) => { redirect = url; return Task.CompletedTask; } });
        var flow = new CodingAgentMcpOAuthFlow(http);
        Assert.Equal(CodingAgentMcpOAuthFlowResult.Redirect, await flow.AuthorizeAsync(provider, new() { ServerUrl = new("https://mcp.test/"), AuthorizationServerMetadataUrl = new("https://config.test/document") }));
        Assert.Equal("new.test", redirect!.Host); Assert.Equal("https://issuer.test", (await provider.DiscoveryStateAsync())!["authorizationServerUrl"]!.GetValue<string>());
        Assert.Contains("https://config.test/document", endpoints);
        await Assert.ThrowsAsync<CodingAgentMcpOAuthInsecureEndpointException>(() => flow.AuthorizeAsync(provider, new() { ServerUrl = new("https://mcp.test/"), AuthorizationServerMetadataUrl = new("http://config.test/document") }));
        Assert.Equal(2, endpoints.Count);
    }

    /// <summary>【CodingAgent】【OAuth 测试缓存】创建已发现服务的内存提供方，避免无关网络步骤。</summary><param name="options">提供方选项。</param><returns>提供方。</returns>
    internal static async Task<CodingAgentMcpOAuthProvider> CachedProviderAsync(CodingAgentMcpOAuthProviderOptions options)
    {
        var provider = new CodingAgentMcpOAuthProvider(options);
        await provider.SaveDiscoveryStateAsync(new() { ["authorizationServerUrl"] = "https://issuer.test", ["authorizationServerMetadata"] = Metadata() });
        return provider;
    }
}
