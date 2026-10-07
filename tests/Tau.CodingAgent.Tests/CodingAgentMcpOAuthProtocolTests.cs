// 作者：xxx
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime.Mcp;
using Tau.CodingAgent.Runtime.Mcp.OAuth;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpOAuthProtocolTests
{
    /// <summary>【CodingAgent】【OAuth 数据兼容】令牌数值和空字段按源协议转换，非法响应在进入存储前失败。</summary>
    [Fact]
    public void McpOAuthJsonValidatesAndNormalizesProtocolData()
    {
        foreach (var (value, expected) in new[] { ("\"1.5\"", 1.5), ("12", 12d), ("\"0x10\"", 16d), ("true", 1d), ("[]", 0d), ("[\"2\"]", 2d), ("\" \"", 0d), ("-3", -3d) })
        {
            var input = JsonNode.Parse("""{"access_token":"fixture","token_type":"Bearer","scope":"","refresh_token":null,"unknown":"drop"}""")!.AsObject();
            input["expires_in"] = JsonNode.Parse(value);
            var tokens = CodingAgentMcpOAuthJson.Tokens(input);
            Assert.Equal(expected, tokens["expires_in"]!.GetValue<double>()); Assert.False(tokens.ContainsKey("scope"));
            Assert.False(tokens.ContainsKey("refresh_token")); Assert.False(tokens.ContainsKey("unknown")); Assert.True(input.ContainsKey("unknown"));
        }
        foreach (var value in new[] { "\"bad\"", "{}", "[1,2]", "\"Infinity\"" })
            Assert.Throws<InvalidDataException>(() => CodingAgentMcpOAuthJson.Tokens(JsonNode.Parse($$"""{"access_token":"x","token_type":"Bearer","expires_in":{{value}}}""")));
        Assert.Throws<InvalidDataException>(() => CodingAgentMcpOAuthJson.Tokens(new JsonObject { ["access_token"] = "", ["token_type"] = "Bearer" }));
        var client = CodingAgentMcpOAuthJson.ClientInformation(new JsonObject { ["client_id"] = "fixture", ["client_secret"] = "", ["client_id_issued_at"] = 123, ["client_secret_expires_at"] = "123", ["extra"] = true });
        Assert.Equal(123, client["client_id_issued_at"]!.GetValue<int>()); Assert.False(client.ContainsKey("client_secret_expires_at"));
        Assert.True(client["extra"]!.GetValue<bool>()); Assert.Empty(client["redirect_uris"]!.AsArray()); Assert.False(client.ContainsKey("client_secret"));
        Assert.Throws<InvalidDataException>(() => CodingAgentMcpOAuthJson.ProtectedResource(new JsonObject { ["resource"] = "javascript:alert(1)" }));
        var metadata = Metadata(); metadata["client_id_metadata_document_supported"] = "true"; metadata["unknown"] = new JsonObject { ["preserved"] = true };
        var parsed = CodingAgentMcpOAuthJson.AuthorizationServer(metadata);
        Assert.False(parsed.ContainsKey("client_id_metadata_document_supported")); Assert.True(parsed["unknown"]!["preserved"]!.GetValue<bool>());
        metadata.Remove("response_types_supported"); Assert.Throws<InvalidDataException>(() => CodingAgentMcpOAuthJson.AuthorizationServer(metadata));
    }

    /// <summary>【CodingAgent】【OAuth 质询与资源】只识别支持的认证方案，绑定完整父路径，权限升级保留已有授权。</summary>
    [Fact]
    public void McpOAuthChallengeAndResourceBindingRejectAmbiguousIdentities()
    {
        var challenge = CodingAgentMcpOAuthDiscovery.ParseChallenge("  DPoP resource_metadata=\"https://mcp.test/meta\", scope=\"read write\", error=insufficient_scope, error_description=\"more needed\"");
        Assert.Equal("https://mcp.test/meta", challenge.ResourceMetadataUrl!.AbsoluteUri); Assert.Equal("read write", challenge.Scope);
        Assert.Equal("insufficient_scope", challenge.Error); Assert.Equal("more needed", challenge.ErrorDescription);
        Assert.Null(CodingAgentMcpOAuthDiscovery.ParseChallenge("Basic scope=\"read\"").Scope);
        Assert.Null(CodingAgentMcpOAuthDiscovery.ParseChallenge("Bearer scope=\"\", resource_metadata=\"not a uri\"").ResourceMetadataUrl);
        Assert.Null(CodingAgentMcpOAuthDiscovery.ParseChallenge("Bearer scope=\"\"").Scope);
        Assert.Equal("read write admin", CodingAgentMcpOAuthFlow.StepUpScope("read  write", "write\tadmin")); Assert.Null(CodingAgentMcpOAuthFlow.StepUpScope("read", ""));
        var resource = new JsonObject { ["resource"] = "https://mcp.test/api" };
        Assert.Equal("https://mcp.test/api", CodingAgentMcpOAuthDiscovery.SelectResource(new("https://mcp.test/api/tools?q=1#fragment"), resource));
        Assert.Throws<InvalidDataException>(() => CodingAgentMcpOAuthDiscovery.SelectResource(new("https://mcp.test/apix"), resource));
        Assert.Throws<InvalidDataException>(() => CodingAgentMcpOAuthDiscovery.SelectResource(new("https://other.test/api"), resource));
    }

    /// <summary>【CodingAgent】【OAuth 发现回退】资源路径缺失和 OAuth 缺失按顺序回退，发行方大小写不同也不被默许。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthDiscoveryUsesOrderedFallbacksAndValidatesIssuer()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal(CodingAgentMcpClient.LatestProtocolVersion, request.Headers.GetValues("MCP-Protocol-Version").Single());
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/.well-known/oauth-protected-resource/api/mcp" => Response(404, "{}"),
                "/.well-known/oauth-protected-resource" => Response(200, """{"resource":"https://mcp.test/api","authorization_servers":["https://issuer.test/tenant/"],"scopes_supported":["read"]}"""),
                "/.well-known/oauth-authorization-server/tenant" => Response(502, ""),
                "/.well-known/openid-configuration/tenant" => Response(404, ""),
                "/tenant/.well-known/openid-configuration" => Response(200, Metadata("https://issuer.test/tenant").ToJsonString()),
                _ => throw new InvalidOperationException("Unexpected URL")
            });
        }));
        var discovery = new CodingAgentMcpOAuthDiscovery(http);
        var found = await discovery.DiscoverAsync(new("https://mcp.test/api/mcp?x=1"));
        Assert.Equal("https://issuer.test/tenant/", found["authorizationServerUrl"]!.GetValue<string>());
        Assert.Equal(5, calls.Count); Assert.Equal("/tenant/.well-known/openid-configuration", calls[^1]);
        using var mismatch = new HttpClient(new Handler((_, _) => Task.FromResult(Response(200, Metadata("https://ISSUER.test/tenant").ToJsonString()))));
        await Assert.ThrowsAsync<CodingAgentMcpOAuthIssuerException>(() => new CodingAgentMcpOAuthDiscovery(mismatch).AuthorizationServerAsync("https://issuer.test/tenant"));
        Assert.NotNull(await new CodingAgentMcpOAuthDiscovery(mismatch).AuthorizationServerAsync("https://issuer.test/tenant", skipIssuerValidation: true));
    }

    /// <summary>【CodingAgent】【OAuth 发现失败边界】显式文档不回退，500 中止授权发现，网络失败和取消不被缺省流程吞掉。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpOAuthDiscoverySeparatesMissingDocumentsFromNetworkFailures()
    {
        var calls = 0;
        using var missing = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Response(404, "{}")); }));
        var discovery = new CodingAgentMcpOAuthDiscovery(missing);
        await Assert.ThrowsAsync<InvalidDataException>(() => discovery.ProtectedResourceAsync(new("https://mcp.test/api"), new("https://mcp.test/custom")));
        Assert.Equal(1, calls);
        var info = await discovery.DiscoverAsync(new("https://mcp.test/"));
        Assert.Equal("https://mcp.test/", info["authorizationServerUrl"]!.GetValue<string>()); Assert.Null(info["authorizationServerMetadata"]);
        using var failing = new HttpClient(new Handler((_, _) => Task.FromResult(Response(500, "private"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new CodingAgentMcpOAuthDiscovery(failing).AuthorizationServerAsync("https://issuer.test"));
        using var network = new HttpClient(new Handler((_, _) => throw new HttpRequestException("network")));
        await Assert.ThrowsAsync<HttpRequestException>(() => new CodingAgentMcpOAuthDiscovery(network).DiscoverAsync(new("https://mcp.test")));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.DiscoverAsync(new("https://mcp.test"), token: cancellation.Token));
    }

    /// <summary>【CodingAgent】【OAuth 测试元数据】创建不含真实账号的发行方与能力。</summary><param name="issuer">测试发行方。</param><returns>元数据。</returns>
    internal static JsonObject Metadata(string issuer = "https://issuer.test") => new()
    {
        ["issuer"] = issuer, ["authorization_endpoint"] = "https://issuer.test/authorize?keep=yes&state=old", ["token_endpoint"] = "https://issuer.test/token",
        ["registration_endpoint"] = "https://issuer.test/register", ["response_types_supported"] = new JsonArray("code"), ["code_challenge_methods_supported"] = new JsonArray("S256")
    };
    /// <summary>【CodingAgent】【OAuth 测试响应】创建由生产代码释放的 HTTP 响应。</summary><param name="status">状态。</param><param name="body">JSON 或错误正文。</param><returns>响应。</returns>
    internal static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    /// <summary>【CodingAgent】【OAuth 表单观察】解析测试中的编码参数。</summary><param name="text">编码表单或 URL 查询。</param><returns>参数字典。</returns>
    internal static Dictionary<string, string> Form(string text) => text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(value => value.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString((pair.Length > 1 ? pair[1] : "").Replace('+', ' ')));
    /// <summary>【CodingAgent】【OAuth 模拟 HTTP】只响应测试委托，不访问外部服务。</summary><param name="send">测试处理器。</param>
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>【CodingAgent】【OAuth 模拟发送】转交模拟请求。</summary><param name="request">请求。</param><param name="token">取消。</param><returns>测试响应。</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
