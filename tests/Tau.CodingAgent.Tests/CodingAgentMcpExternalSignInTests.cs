// 作者：xxx
using System.Text.Json.Nodes;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;
using Tau.CodingAgent.Runtime.Mcp.OAuth;
using static Tau.CodingAgent.Tests.CodingAgentMcpOAuthProtocolTests;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【外部登录观察】客户端元数据及其他账号不触发重连，失效令牌只尝试一次，并发扫描合并同一连接恢复。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpExternalSignInReconnectsOnlyWhenOwnTokensChange()
    {
        using var temp = TempDirectory.Create(); var initialized = 0;
        using var http = ExternalSignInHttp(() => Interlocked.Increment(ref initialized));
        var registry = new CodingAgentMcpServerRegistry(); registry.Register("fixture", new() { ["url"] = "https://mcp.test/" }, "fixture");
        var credentials = new CodingAgentMcpOAuthCredentialStore(temp.Path);
        await using var service = new CodingAgentMcpService(temp.Path, temp.Path, () => true, registry, new() { HttpClient = http });
        await service.ReloadAsync(); await service.WaitForServersAsync();
        Assert.Equal("needs-auth", Assert.Single(service.GetStatus()).State); Assert.Equal(1, initialized);
        await credentials.ForServer("fixture", "https://mcp.test/").SaveAsync(new() { ["serverUrl"] = "https://mcp.test/", ["clientInformation"] = new JsonObject { ["client_id"] = "changed" } });
        await SaveExternalTokenAsync(credentials, "other", "fixture-login");
        await service.ReconnectSignedInServersAsync(); Assert.Equal(1, initialized);
        await SaveExternalTokenAsync(credentials, "fixture", "invalid");
        await service.ReconnectSignedInServersAsync(); Assert.Equal(2, initialized);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.ReconnectSignedInServersAsync()));
        Assert.Equal(2, initialized); Assert.Equal("needs-auth", Assert.Single(service.GetStatus()).State);
        await SaveExternalTokenAsync(credentials, "fixture", "fixture-login");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.ReconnectSignedInServersAsync()));
        Assert.Equal(3, initialized); Assert.Equal("connected", Assert.Single(service.GetStatus()).State);
        Assert.Single(service.GetTools());
        Assert.Equal("fixture-login", (await credentials.TokensAsync("other", "https://mcp.test/"))!["access_token"]!.GetValue<string>());
    }

    /// <summary>【CodingAgent】【外部登录回合边界】SDK 下一次模型请求自动恢复连接并声明新工具，无需手工重启会话。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpExternalSignInRefreshesToolsBeforeTheNextModelRequest()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); var initialized = 0;
        CodingAgentMcpConfiguration.Add(Path.Combine(agent, "mcp.json"), "fixture", new() { ["url"] = "https://mcp.test/", ["exposure"] = "direct" });
        using var http = ExternalSignInHttp(() => Interlocked.Increment(ref initialized));
        var contexts = new List<LlmContext>();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, IncludeExtensions = false, EnableMcp = true,
            ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = CreatePromptCapturingRegistry(contexts.Add),
            ProviderId = "test-provider", ModelId = "test-model", McpOptions = new() { HttpClient = http }
        });
        await session.Mcp!.WaitForServersAsync();
        await foreach (var ignored in session.RunAsync("before sign in")) { }
        Assert.DoesNotContain(contexts[^1].Tools ?? [], tool => tool.Name == "mcp__fixture__echo");
        await SaveExternalTokenAsync(new(agent), "fixture", "fixture-login");
        await foreach (var ignored in session.RunAsync("after sign in")) { }
        Assert.Contains(contexts[^1].Tools ?? [], tool => tool.Name == "mcp__fixture__echo");
        Assert.Equal("connected", Assert.Single(session.Mcp.GetStatus()).State); Assert.Equal(2, initialized);
    }

    /// <summary>【CodingAgent】【运行期认证恢复】工具请求认证失败也记录旧令牌，外部保存新令牌后恢复，旧目录不会丢失。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpExternalSignInRecoversAuthenticationFailureDuringToolCall()
    {
        using var temp = TempDirectory.Create(); var initialized = 0; var reject = true;
        var credentials = new CodingAgentMcpOAuthCredentialStore(temp.Path); await SaveExternalTokenAsync(credentials, "fixture", "fixture-login");
        using var http = ExternalSignInHttp(() => Interlocked.Increment(ref initialized), () => reject);
        var registry = new CodingAgentMcpServerRegistry(); registry.Register("fixture", new() { ["url"] = "https://mcp.test/" }, "fixture");
        await using var service = new CodingAgentMcpService(temp.Path, temp.Path, () => true, registry, new() { HttpClient = http });
        await service.ReloadAsync(); await service.WaitForServersAsync();
        var tool = Assert.Single(service.GetTools());
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync("denied", ParseMcpSessionJson("{}")));
        Assert.Equal("needs-auth", Assert.Single(service.GetStatus()).State);
        await service.ReconnectSignedInServersAsync(); Assert.Equal(1, initialized);
        reject = false; await SaveExternalTokenAsync(credentials, "fixture", "fixture-login-new");
        await service.ReconnectSignedInServersAsync();
        Assert.Equal(2, initialized); Assert.Equal("connected", Assert.Single(service.GetStatus()).State);
        var result = await Assert.Single(service.GetTools()).ExecuteAsync("restored", ParseMcpSessionJson("{}"));
        Assert.Equal("ok", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
    }

    /// <summary>【CodingAgent】【外部登录关闭】凭据文件锁阻塞扫描时，取消或关闭仍排空观察任务，不发起额外连接。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpExternalSignInScanCancelsAndDrainsOnServiceClose()
    {
        using var temp = TempDirectory.Create(); var initialized = 0;
        using var http = ExternalSignInHttp(() => Interlocked.Increment(ref initialized));
        var registry = new CodingAgentMcpServerRegistry(); registry.Register("fixture", new() { ["url"] = "https://mcp.test/" }, "fixture");
        await using var service = new CodingAgentMcpService(temp.Path, temp.Path, () => true, registry, new() { HttpClient = http });
        await service.ReloadAsync(); await service.WaitForServersAsync();
        await using var held = new FileStream(Path.Combine(temp.Path, "mcp-auth.json.tau-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancel = new CancellationTokenSource();
        var first = service.ReconnectSignedInServersAsync(cancel.Token);
        await Task.Delay(40); Assert.False(first.IsCompleted);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = service.ReconnectSignedInServersAsync();
        await Task.Delay(40); Assert.False(second.IsCompleted);
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, initialized);
    }

    /// <summary>【CodingAgent】【外部登录样本】模拟另一个宿主保存独立服务器的令牌。</summary>
    /// <param name="credentials">共享凭据文件。</param><param name="name">服务器名。</param><param name="token">测试令牌。</param><returns>写入任务。</returns>
    private static Task SaveExternalTokenAsync(CodingAgentMcpOAuthCredentialStore credentials, string name, string token) =>
        credentials.ForServer(name, "https://mcp.test/").SaveAsync(new() { ["serverUrl"] = "https://mcp.test/",
            ["tokens"] = new JsonObject { ["access_token"] = token, ["token_type"] = "Bearer" } });

    /// <summary>【CodingAgent】【外部登录模拟】只接受测试令牌，记录初始化次数并可注入运行期拒绝。</summary>
    /// <param name="initialize">初始化计数。</param><param name="rejectTool">工具是否拒绝认证。</param><returns>模拟 HTTP 客户端。</returns>
    private static HttpClient ExternalSignInHttp(Action initialize, Func<bool>? rejectTool = null) => new(new Handler(async (request, token) =>
    {
        if (request.Method == HttpMethod.Get) return Response(405, "");
        if (request.Method == HttpMethod.Delete) return Response(204, "");
        var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
        var method = message["method"]!.GetValue<string>(); if (method == "initialize") initialize();
        if (request.Headers.Authorization?.Parameter is not ("fixture-login" or "fixture-login-new") || method == "tools/call" && rejectTool?.Invoke() == true)
            return Response(401, "");
        if (!message.ContainsKey("id")) return Response(202, "");
        var result = method switch
        {
            "initialize" => JsonNode.Parse("""{"protocolVersion":"2025-11-25","capabilities":{"tools":{}},"serverInfo":{"name":"fixture","version":"1"}}"""),
            "tools/list" => JsonNode.Parse("""{"tools":[{"name":"echo","inputSchema":{"type":"object"}}]}"""),
            _ => JsonNode.Parse("""{"content":[{"type":"text","text":"ok"}]}""")
        };
        return Response(200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
    }));
}
