// 作者：xxx
using System.Net;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;
using Tau.Tui.Components;
using static Tau.CodingAgent.Tests.CodingAgentMcpOAuthProtocolTests;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【MCP 会话覆盖】扩展服务器禁用跨普通重载保留，策略变化复用连接，扩展新配置重置旧会话覆盖。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpManagementKeepsSessionOverridesAndDoesNotReconnectForExposure()
    {
        using var temp = TempDirectory.Create(); var started = 0;
        using var http = McpManagementHttp(() => Interlocked.Increment(ref started));
        var registry = new CodingAgentMcpServerRegistry();
        var config = new JsonObject { ["url"] = "https://mcp.test/", ["exposure"] = "codemode" };
        registry.Register("fixture", config, "extension");
        await using var service = new CodingAgentMcpService(temp.Path, temp.Path, () => true, registry, new() { HttpClient = http });
        await service.ReloadAsync(); await service.WaitForServersAsync(); Assert.Equal(1, started);
        await service.UpdateServerAsync("fixture", exposure: "direct"); Assert.Equal(1, started); Assert.Equal("direct", Assert.Single(service.GetTools()).Exposure);
        await service.ReloadAsync(); Assert.Equal("direct", Assert.Single(service.GetTools()).Exposure); Assert.Equal(1, started);
        await service.UpdateServerAsync("fixture", enabled: false); Assert.Equal("disabled", Assert.Single(service.GetStatus()).State);
        registry.Register("fixture", config, "extension"); await service.ReloadAsync(); await service.WaitForServersAsync();
        Assert.Equal("disabled", Assert.Single(service.GetStatus()).State); Assert.Equal(1, started); Assert.Empty(service.GetTools());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReconnectAsync("fixture"));
        await service.UpdateServerAsync("fixture", enabled: true); Assert.Equal(2, started);
        config["url"] = "https://mcp.test/changed"; registry.Register("fixture", config, "extension");
        await service.ReloadAsync(); await service.WaitForServersAsync();
        Assert.Equal(3, started); Assert.Equal("codemode", Assert.Single(service.GetServerEntries()).Config["exposure"]!.GetValue<string>());
        Assert.Equal("deferred", Assert.Single(service.GetTools()).Exposure);
        Assert.False(File.Exists(Path.Combine(temp.Path, "mcp.json")));
    }

    /// <summary>【CodingAgent】【MCP 项目覆盖】可信项目只保存轻量覆盖，不重写全局传输和认证字段，普通修改继续写入已有项目覆盖。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpManagementPersistsProjectOverridesWithoutChangingGlobalDefinition()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent");
        var path = Path.Combine(agent, "mcp.json"); var started = 0; var trusted = false;
        CodingAgentMcpConfiguration.Add(path, "fixture", new() { ["url"] = "https://mcp.test/", ["headers"] = new JsonObject { ["X-Fixture"] = "keep" } });
        var original = File.ReadAllText(path);
        using var http = McpManagementHttp(() => Interlocked.Increment(ref started));
        await using var service = new CodingAgentMcpService(agent, temp.Path, () => trusted, new(), new() { HttpClient = http });
        await service.ReloadAsync(); await service.WaitForServersAsync();
        Assert.False(service.CanCreateProjectOverride("fixture"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateServerAsync("fixture", enabled: false, inProject: true));
        trusted = true; await service.UpdateServerAsync("fixture", enabled: false, inProject: true);
        var project = Path.Combine(temp.Path, ".tau", "mcp.json");
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(JsonNode.Parse(File.ReadAllText(project))!["mcpServers"]!["fixture"]!["enabled"]!.GetValue<bool>());
        await service.UpdateServerAsync("fixture", enabled: true); await service.UpdateServerAsync("fixture", exposure: "direct");
        var patch = JsonNode.Parse(File.ReadAllText(project))!["mcpServers"]!["fixture"]!.AsObject();
        Assert.Equal(2, patch.Count); Assert.True(patch["enabled"]!.GetValue<bool>()); Assert.Equal("direct", patch["exposure"]!.GetValue<string>());
        Assert.Equal(original, File.ReadAllText(path)); Assert.Equal(2, started);
        await service.ReconnectAsync("fixture"); Assert.Equal(3, started);
    }

    /// <summary>【CodingAgent】【MCP 管理命令】SDK 会话的命令路由支持状态、重连和策略菜单，工具声明跟随策略且无需重连。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpManagementRouterConnectsMenuChangesToActiveTools()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); var started = 0;
        CodingAgentMcpConfiguration.Add(Path.Combine(agent, "mcp.json"), "fixture", new() { ["url"] = "https://mcp.test/" });
        using var http = McpManagementHttp(() => Interlocked.Increment(ref started));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog(), EnableMcp = true, IncludeExtensions = false, McpOptions = new() { HttpClient = http } });
        await session.Mcp!.WaitForServersAsync();
        var basic = new CodingAgentCommandRouter(session.Runner);
        Assert.Contains("fixture: connected", (await basic.TryHandleAsync("/mcp")).Message);
        Assert.True((await basic.TryHandleAsync("/mcp nonsense")).IsError);
        Assert.False((await basic.TryHandleAsync("/mcp reconnect fixture")).IsError); Assert.Equal(2, started);
        var choices = new Queue<string?>(["fixture", "exposure", "direct", null, null]);
        var menu = new CodingAgentCommandRouter(session.Runner, mcpMenuSelector: (build, _) =>
        {
            var state = build(); var choice = choices.Dequeue();
            if (choice is not null) Assert.Contains(state.Items, item => item.Value == choice);
            Assert.False(string.IsNullOrEmpty(state.Title)); return Task.FromResult(choice);
        });
        Assert.False((await menu.TryHandleAsync("/mcp")).IsError); Assert.Empty(choices); Assert.Equal(2, started);
        Assert.Contains("mcp__fixture__echo", session.Runner.GetActiveToolNames());
        await session.Mcp.UpdateServerAsync("fixture", exposure: "deferred");
        Assert.DoesNotContain("mcp__fixture__echo", session.Runner.GetActiveToolNames()); Assert.Contains("tool_search", session.Runner.GetActiveToolNames());
        Assert.Equal(2, started);
    }

    /// <summary>【CodingAgent】【MCP 菜单动态绘制】状态更新后保留选中项，窄屏换行且取消键结束菜单。</summary>
    [Fact]
    public void McpManagementMenuPreservesSelectionAcrossStatusRefresh()
    {
        var status = "connecting";
        var component = new CodingAgentMcpMenuSelector.MenuComponent(() => new("MCP servers", [new("first", "服务器一", status), new("second", "服务器二", status)], "选择服务器", "first"));
        component.HandleInput(new('\0', ConsoleKey.DownArrow, false, false, false));
        status = "connected"; Assert.True(component.Refresh());
        Assert.Contains(component.Render(28), line => line.Contains("MCP servers", StringComparison.Ordinal));
        component.HandleInput(new('\r', ConsoleKey.Enter, false, false, false));
        Assert.True(component.Finished); Assert.Equal("second", component.Value);
        var empty = new CodingAgentMcpMenuSelector.MenuComponent(() => new("Empty", []));
        empty.HandleInput(new('\u001b', ConsoleKey.Escape, false, false, false)); Assert.True(empty.Finished);
    }

    /// <summary>【CodingAgent】【MCP 管理模拟】使用只提供 echo 工具的匿名服务器，记录实际初始化次数。</summary><param name="initialize">初始化计数回调。</param><returns>HTTP 客户端。</returns>
    private static HttpClient McpManagementHttp(Action initialize) => new(new Handler(async (request, token) =>
    {
        if (request.Method == HttpMethod.Get) return Response(405, "");
        if (request.Method == HttpMethod.Delete) return Response(204, "");
        var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
        if (!message.ContainsKey("id")) return Response(202, "");
        var method = message["method"]!.GetValue<string>();
        if (method == "initialize") initialize();
        var result = method == "initialize" ? JsonNode.Parse("""{"protocolVersion":"2025-11-25","capabilities":{"tools":{}},"serverInfo":{"name":"fixture","version":"1"}}""") :
            JsonNode.Parse("""{"tools":[{"name":"echo","description":"管理测试工具","inputSchema":{"type":"object"}}]}""");
        return Response(200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
    }));
}
