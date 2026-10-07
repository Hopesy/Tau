// 作者：xxx
using System.Text.Json.Nodes;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【MCP 选择等待】无关慢服务器不阻塞普通脚本、已就绪命名空间或配置管理，发现接口仍等待全部。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpDiscoveryWaitsOnlyForServersNamedByTheScript()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); var path = Path.Combine(agent, "mcp.json");
        CodingAgentMcpConfiguration.Add(path, "fast", new() { ["url"] = "https://mcp.test/fast" });
        CodingAgentMcpConfiguration.Add(path, "slow", new() { ["url"] = "https://mcp.test/slow" });
        using var http = McpManagementHttp(() => { });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, EnableMcp = true, IncludeExtensions = false, ModelCatalog = CreateModelCatalog(),
            McpOptions = new() { TransportFactory = async (entry, _, token) =>
            {
                if (entry.Name == "slow") await release.Task.WaitAsync(token);
                return new CodingAgentMcpHttpTransport(new Uri(entry.Config["url"]!.GetValue<string>()), http, openGetStream: false);
            } }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.Mcp!.WaitForServerAsync("fast", deadline.Token);
        var code = Assert.Single(session.Runner.GetRegisteredTools(), tool => tool.Name == "codemode");
        var simple = await code.ExecuteAsync("simple", ParseMcpSessionJson("""{"code":"return 42"}"""), deadline.Token);
        Assert.False(simple.IsError); Assert.Contains(simple.Content.OfType<TextContent>(), item => item.Text.EndsWith("\n42", StringComparison.Ordinal));
        var fast = await code.ExecuteAsync("fast", ParseMcpSessionJson("""{"code":"return typeof tools.mcp__fast__echo"}"""), deadline.Token);
        Assert.False(fast.IsError); Assert.Contains(fast.Content.OfType<TextContent>(), item => item.Text.EndsWith("\nfunction", StringComparison.Ordinal));
        await session.Mcp.WaitForScriptServersAsync("const researchTools = 1; return researchTools;", deadline.Token);
        await session.Mcp.UpdateServerAsync("slow", exposure: "deferred", token: deadline.Token);
        await session.Mcp.UpdateServerAsync("fast", enabled: false, token: deadline.Token);
        await session.Mcp.UpdateServerAsync("fast", enabled: true, token: deadline.Token);
        Assert.Equal("connecting", session.Mcp.GetStatus().Single(item => item.Name == "slow").State);
        foreach (var source in new[] { "searchTools('echo')", "describeNamespace('fast')", "describeTool('echo')", "ALL_TOOLS", "tools.mcp__slow__echo({})" })
        {
            using var cancel = new CancellationTokenSource();
            var waiting = session.Mcp.WaitForScriptServersAsync(source, cancel.Token);
            Assert.False(waiting.IsCompleted, source);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        Assert.Equal("connecting", session.Mcp.GetStatus().Single(item => item.Name == "slow").State);
        release.SetResult(); await session.Mcp.WaitForServersAsync(deadline.Token);
        Assert.All(session.Mcp.GetStatus(), item => Assert.Equal("connected", item.State));
    }

    /// <summary>【CodingAgent】【MCP 策略声明】已加载的间接工具在访问策略变更及再次应用同一策略时退出声明集合。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpDiscoveryRemovesLoadedDeclarationsWhenIndirectPolicyIsReapplied()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent");
        CodingAgentMcpConfiguration.Add(Path.Combine(agent, "mcp.json"), "fixture", new() { ["url"] = "https://mcp.test/", ["exposure"] = "deferred" });
        var started = 0; using var http = McpManagementHttp(() => Interlocked.Increment(ref started));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog(), EnableMcp = true, IncludeExtensions = false, McpOptions = new() { HttpClient = http } });
        await session.Mcp!.WaitForServersAsync();
        session.Runner.SetActiveTools(["tool_search", "mcp__fixture__echo"]);
        Assert.Contains("mcp__fixture__echo", session.Runner.GetActiveToolNames());
        await session.Mcp.UpdateServerAsync("fixture", exposure: "codemode");
        Assert.DoesNotContain("mcp__fixture__echo", session.Runner.GetActiveToolNames());
        session.Runner.SetActiveTools(["codemode", "mcp__fixture__echo"]);
        Assert.Contains("mcp__fixture__echo", session.Runner.GetActiveToolNames());
        await session.Mcp.UpdateServerAsync("fixture", exposure: "codemode");
        Assert.DoesNotContain("mcp__fixture__echo", session.Runner.GetActiveToolNames());
        Assert.Equal(1, started);
    }

    /// <summary>【CodingAgent】【MCP 参数补全】动作保留空格，服务器过滤认证类型和禁用状态，已有提供方读取最新注册目录。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpDiscoveryCompletesActionsAndLiveEligibleServerNames()
    {
        using var temp = TempDirectory.Create(); using var http = McpManagementHttp(() => { });
        var registry = new CodingAgentMcpServerRegistry();
        registry.Register("oauth", new() { ["url"] = "https://mcp.test/" }, "fixture");
        registry.Register("manual", new() { ["url"] = "https://mcp.test/", ["headers"] = new JsonObject { ["aUtHoRiZaTiOn"] = "Bearer fixture" } }, "fixture");
        registry.Register("disabled", new() { ["url"] = "https://mcp.test/", ["enabled"] = false }, "fixture");
        registry.Register("provider", new() { ["url"] = "https://mcp.test/", ["auth"] = new JsonObject { ["provider"] = "fixture" } }, "fixture");
        await using var service = new CodingAgentMcpService(temp.Path, temp.Path, () => true, registry, new() { HttpClient = http, ProviderToken = (_, _) => Task.FromResult<string?>("fixture") });
        await service.ReloadAsync(); await service.WaitForServersAsync();
        var provider = CodingAgentAutocompleteProviderFactory.Create(basePath: temp.Path, mcpService: service);
        var actions = (await provider.GetSuggestionsAsync("/mcp ", 5))!;
        Assert.Equal(["login ", "logout ", "reconnect "], actions.Items.Select(item => item.Value));
        var completed = provider.ApplyCompletion("/mcp ", 5, actions.Items[0], actions.Prefix);
        Assert.Equal("/mcp login ", completed.Text);
        var login = (await provider.GetSuggestionsAsync(completed.Text, completed.CursorIndex))!;
        Assert.Equal("oauth", Assert.Single(login.Items).Label); Assert.Equal("connected · 1 tool", login.Items[0].Description);
        Assert.Equal("/mcp login oauth", provider.ApplyCompletion(completed.Text, completed.CursorIndex, login.Items[0], login.Prefix).Text);
        var reconnect = (await provider.GetSuggestionsAsync("/mcp reconnect ", 15))!;
        Assert.Equal(["oauth", "manual", "provider"], reconnect.Items.Select(item => item.Label));
        Assert.Null(await provider.GetSuggestionsAsync("/mcp invalid ", 13));
        Assert.Null(await provider.GetSuggestionsAsync("/mcp login oauth extra", 22));
        registry.Register("other", new() { ["url"] = "https://mcp.test/" }, "fixture"); await service.ReloadAsync(); await service.WaitForServersAsync();
        var updated = (await provider.GetSuggestionsAsync("/mcp logout o", 13))!;
        Assert.Equal(["oauth", "other"], updated.Items.Select(item => item.Label));
        await service.UpdateServerAsync("oauth", enabled: false);
        Assert.Equal("other", Assert.Single((await provider.GetSuggestionsAsync("/mcp login ", 11))!.Items).Label);
    }
}
