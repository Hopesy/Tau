// 作者：xxx
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;
using static Tau.CodingAgent.Tests.CodingAgentMcpOAuthProtocolTests;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【MCP 资源缓存】初次连接遍历分页并过滤界面资源，管理报告不重复读取，通知同步更新菜单数量。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpResourceCatalogCachesPagesAndUpdatesReportsAndMenusFromNotifications()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent");
        CodingAgentMcpConfiguration.Add(Path.Combine(agent, "mcp.json"), "docs", new() { ["url"] = "https://mcp.test/", ["exposure"] = "direct" });
        var phase = 0; var lists = 0;
        using var http = ResourceCatalogHttp(async (method, message, token) =>
        {
            if (method == "resources/read")
            {
                Interlocked.Exchange(ref phase, 1);
                return ResourceCatalogReply(message, JsonNode.Parse("""{"contents":[{"uri":"fixture://one","text":"ok"}]}""")!, notify: true);
            }
            Interlocked.Increment(ref lists);
            if (method == "resources/templates/list") return ResourceCatalogReply(message, JsonNode.Parse(
                phase == 0 ? """{"resourceTemplates":[{"uriTemplate":"fixture://{id}"},{"uriTemplate":"ui://{id}"},{"uriTemplate":"fixture://app/{id}","mimeType":"text/html; profile=mcp-app"}]}""" :
                    """{"resourceTemplates":[{"uriTemplate":"fixture://{id}"},{"uriTemplate":"fixture://new/{id}"}]}""")!);
            if (message["params"]?["cursor"] is not null) return ResourceCatalogReply(message, JsonNode.Parse("""{"resources":[{"uri":"fixture://two"},{"uri":"ui://panel"}]}""")!);
            await Task.Yield(); token.ThrowIfCancellationRequested();
            return ResourceCatalogReply(message, JsonNode.Parse(phase == 0 ?
                """{"resources":[{"uri":"fixture://one"},{"uri":"fixture://app","mimeType":"text/html; PROFILE = \"mcp-app\""}],"nextCursor":"next"}""" :
                """{"resources":[{"uri":"fixture://one"},{"uri":"fixture://two"},{"uri":"fixture://three"}]}""")!);
        });
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog(), EnableMcp = true, IncludeExtensions = false, McpOptions = new() { HttpClient = http } });
        await session.Mcp!.WaitForServersAsync();
        var status = Assert.Single(session.Mcp.GetStatus());
        Assert.Equal(2, status.ResourceCount); Assert.Equal(1, status.ResourceTemplateCount); Assert.Equal(3, lists);
        var report = (await session.Mcp.GetReportsAsync())[0]!;
        Assert.Equal(2, report["resources"]!.GetValue<int>()); Assert.Equal(1, report["resourceTemplates"]!.GetValue<int>());
        await session.Mcp.GetReportsAsync(); Assert.Equal(3, lists);
        var version = session.Mcp.Version;
        var read = Assert.Single(session.Runner.GetRegisteredTools(), tool => tool.Name == "read_mcp_resource");
        await read.ExecuteAsync("refresh", ParseMcpSessionJson("""{"server":"docs","uri":"fixture://one"}"""));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (session.Mcp.Version == version) await Task.Delay(10, deadline.Token);
        status = Assert.Single(session.Mcp.GetStatus());
        Assert.Equal(3, status.ResourceCount); Assert.Equal(2, status.ResourceTemplateCount); Assert.Equal(5, lists);
        report = (await session.Mcp.GetReportsAsync())[0]!;
        Assert.Equal(3, report["resources"]!.GetValue<int>()); Assert.Equal(2, report["resourceTemplates"]!.GetValue<int>());
        var summary = (await new CodingAgentCommandRouter(session.Runner).TryHandleAsync("/mcp")).Message;
        Assert.Contains("3 resources", summary); Assert.Contains("2 URI templates", summary); Assert.Equal(5, lists);
    }

    /// <summary>【CodingAgent】【MCP 资源失败隔离】资源与模板的失败互不覆盖，缓存为空不影响服务器连接，按需请求仍报告原错误。</summary>
    /// <param name="failResources">失败的是普通资源还是模板。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task McpResourceCatalogKeepsConnectedWhenOneListFails(bool failResources)
    {
        using var temp = TempDirectory.Create();
        using var http = ResourceCatalogHttp((method, message, _) =>
        {
            if ((method == "resources/list") == failResources)
                return Task.FromResult(Response(200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32001, ["message"] = "fixture listing failure" } }.ToJsonString()));
            return Task.FromResult(ResourceCatalogReply(message, JsonNode.Parse(method == "resources/list" ?
                """{"resources":[{"uri":"fixture://one"}]}""" : """{"resourceTemplates":[{"uriTemplate":"fixture://{id}"}]}""")!));
        });
        var registry = new CodingAgentMcpServerRegistry(); registry.Register("docs", new() { ["url"] = "https://mcp.test/" }, "fixture");
        await using var service = new CodingAgentMcpService(temp.Path, temp.Path, () => true, registry, new() { HttpClient = http });
        await service.ReloadAsync(); await service.WaitForServersAsync();
        var status = Assert.Single(service.GetStatus()); Assert.Equal("connected", status.State); Assert.Null(status.Error);
        Assert.Equal(failResources ? 0 : 1, status.ResourceCount); Assert.Equal(failResources ? 1 : 0, status.ResourceTemplateCount);
        Assert.Null((await service.GetReportsAsync())[0]!["error"]);
        var tool = service.GetResourceTools().Single(item => item.Name == (failResources ? "list_mcp_resources" : "list_mcp_resource_templates"));
        var error = await Assert.ThrowsAsync<CodingAgentMcpException>(() => tool.ExecuteAsync("list", ParseMcpSessionJson("""{"server":"docs"}""")));
        Assert.Contains("fixture listing failure", error.Message);
    }

    /// <summary>【CodingAgent】【MCP 资源刷新关闭】通知触发的挂起目录请求受服务生命周期取消，关闭后不发布旧快照。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpResourceCatalogRefreshDrainsWhenServiceCloses()
    {
        using var temp = TempDirectory.Create(); var refreshing = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = ResourceCatalogHttp(async (method, message, token) =>
        {
            if (method == "resources/read")
            {
                refreshing = true;
                return ResourceCatalogReply(message, JsonNode.Parse("""{"contents":[]}""")!, notify: true);
            }
            if (refreshing) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return ResourceCatalogReply(message, JsonNode.Parse(method == "resources/list" ? """{"resources":[]}""" : """{"resourceTemplates":[]}""")!);
        });
        var registry = new CodingAgentMcpServerRegistry(); registry.Register("docs", new() { ["url"] = "https://mcp.test/" }, "fixture");
        await using var service = new CodingAgentMcpService(temp.Path, temp.Path, () => true, registry, new() { HttpClient = http });
        await service.ReloadAsync(); await service.WaitForServersAsync();
        var tool = service.GetResourceTools().Single(item => item.Name == "read_mcp_resource");
        await tool.ExecuteAsync("notify", ParseMcpSessionJson("""{"server":"docs","uri":"fixture://one"}"""));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(service.GetStatus());
    }

    /// <summary>【CodingAgent】【资源测试传输】模拟只具有资源能力的 HTTP 服务器，资源方法由测试控制。</summary>
    /// <param name="resources">资源响应函数。</param><returns>模拟客户端。</returns>
    private static HttpClient ResourceCatalogHttp(Func<string, JsonObject, CancellationToken, Task<HttpResponseMessage>> resources) =>
        new(new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return Response(405, "");
            if (request.Method == HttpMethod.Delete) return Response(204, "");
            var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            if (!message.ContainsKey("id")) return Response(202, "");
            var method = message["method"]!.GetValue<string>();
            if (method == "initialize") return ResourceCatalogReply(message,
                JsonNode.Parse("""{"protocolVersion":"2025-11-25","capabilities":{"resources":{}},"serverInfo":{"name":"docs","version":"1"}}""")!);
            return await resources(method, message, token);
        }));

    /// <summary>【CodingAgent】【资源测试响应】构造 JSON-RPC 结果，可在 SSE 结果前发送资源变更通知。</summary>
    /// <param name="request">原请求。</param><param name="result">协议结果。</param><param name="notify">是否先发通知。</param><returns>HTTP 响应。</returns>
    private static HttpResponseMessage ResourceCatalogReply(JsonObject request, JsonNode result, bool notify = false)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result }.ToJsonString();
        return notify ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
            "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/resources/list_changed\"}\n\nevent: message\ndata: " + body + "\n\n",
            Encoding.UTF8, "text/event-stream") } : Response(200, body);
    }
}
