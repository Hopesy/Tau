// 作者：xxx
using System.Net;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;
using Tau.CodingAgent.Runtime.Mcp.OAuth;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【MCP】【禁用认证边界】命令显式名称与 SDK 认证入口均拒绝禁用服务器，不启动浏览器或删除凭据。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpManagementRejectsDisabledAuthenticationBeforeAnySideEffect()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); var initialized = 0;
        CodingAgentMcpConfiguration.Add(Path.Combine(agent, "mcp.json"), "disabled", new() { ["url"] = "https://mcp.test/", ["enabled"] = false });
        var credentials = new CodingAgentMcpOAuthCredentialStore(agent); await SaveExternalTokenAsync(credentials, "disabled", "fixture-login");
        using var http = McpManagementHttp(() => initialized++);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog(),
            EnableMcp = true, IncludeExtensions = false, McpOptions = new() { HttpClient = http }
        });
        await session.Mcp!.WaitForServersAsync();
        var router = new CodingAgentCommandRouter(session.Runner, oauthLoginCallbacksFactory: () => throw new Exception("must not open login"));
        foreach (var action in new[] { "login", "logout", "reconnect" })
        {
            Assert.True((await router.TryHandleAsync("/mcp " + action + " disabled")).IsError);
            Assert.Contains("No MCP server named", (await router.TryHandleAsync("/mcp " + action + " missing")).Message);
        }
        var prompt = new CodingAgentMcpOAuthSignInTests.Prompt((_, _) => throw new Exception("must not open login"), _ => Task.FromResult<string?>(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Mcp.SignInAsync("disabled", prompt));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Mcp.SignOutAsync("disabled"));
        Assert.Equal(0, initialized);
        Assert.NotNull(await credentials.TokensAsync("disabled", "https://mcp.test/"));
    }

    /// <summary>【MCP】【注销网络边界】匿名也能访问的服务器注销后仍停留在待登录状态，保留目录，新外部令牌才自动恢复。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpManagementSignOutPreservesCatalogWithoutAnonymousReconnect()
    {
        using var temp = TempDirectory.Create(); var initialized = 0;
        using var http = McpManagementHttp(() => Interlocked.Increment(ref initialized));
        var credentials = new CodingAgentMcpOAuthCredentialStore(temp.Path); await SaveExternalTokenAsync(credentials, "fixture", "fixture-login");
        var registry = new CodingAgentMcpServerRegistry(); registry.Register("fixture", new() { ["url"] = "https://mcp.test/" }, "fixture");
        await using var service = new CodingAgentMcpService(temp.Path, temp.Path, () => true, registry, new() { HttpClient = http });
        await service.ReloadAsync(); await service.WaitForServersAsync();
        var original = Assert.Single(service.GetTools()).Name;
        await service.SignOutAsync("fixture");
        Assert.Equal(1, initialized); Assert.Equal("needs-auth", Assert.Single(service.GetStatus()).State);
        Assert.Equal(original, Assert.Single(service.GetTools()).Name);
        Assert.Equal(1, Assert.Single(service.GetStatus()).ToolCount);
        Assert.Null(await credentials.TokensAsync("fixture", "https://mcp.test/"));
        await service.ReconnectSignedInServersAsync(); Assert.Equal(1, initialized);
        await SaveExternalTokenAsync(credentials, "fixture", "fixture-login-new");
        await service.ReconnectSignedInServersAsync();
        Assert.Equal(2, initialized); Assert.Equal("connected", Assert.Single(service.GetStatus()).State);
    }

    /// <summary>【MCP】【命令优先选择】无名称时优先选择唯一故障连接，重连仍待登录时报错，注销优先选择待登录账号。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpManagementSelectsPreferredServerAndReportsUnresolvedAuthentication()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); var failed = true; var connections = new List<string>();
        foreach (var name in new[] { "healthy", "failed", "login", "disabled" })
            CodingAgentMcpConfiguration.Add(Path.Combine(agent, "mcp.json"), name, new() { ["url"] = "https://mcp.test/" + name, ["enabled"] = name != "disabled" });
        using var http = McpManagementHttp(() => { });
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog(), EnableMcp = true, IncludeExtensions = false,
            McpOptions = new() { TransportFactory = (entry, _, _) =>
            {
                lock (connections) connections.Add(entry.Name);
                if (entry.Name == "failed" && failed) throw new InvalidOperationException("fixture unavailable");
                if (entry.Name == "login") throw new CodingAgentMcpHttpException(HttpStatusCode.Unauthorized, "fixture login");
                return Task.FromResult<ICodingAgentMcpTransport>(new CodingAgentMcpHttpTransport(new(entry.Config["url"]!.GetValue<string>()), http, openGetStream: false));
            } }
        });
        await session.Mcp!.WaitForServersAsync(); failed = false;
        var router = new CodingAgentCommandRouter(session.Runner, mcpMenuSelector: (_, _) => throw new Exception("unique preferred server must not prompt"));
        Assert.Contains("failed: connected", (await router.TryHandleAsync("/mcp reconnect")).Message);
        Assert.Equal(2, connections.Count(name => name == "failed")); Assert.Equal(1, connections.Count(name => name == "healthy"));
        var rejected = await router.TryHandleAsync("/mcp reconnect login");
        Assert.True(rejected.IsError); Assert.Contains("still requires sign-in", rejected.Message);
        var before = connections.Count;
        Assert.False((await router.TryHandleAsync("/mcp logout")).IsError);
        Assert.Equal(before, connections.Count);

        // 1. 【MCP】【菜单状态】已连接、待登录和禁用服务器只展示当前可执行动作
        var choices = new Queue<string?>(["healthy", null, "login", null, "disabled", null, null]);
        var menus = new CodingAgentCommandRouter(session.Runner, mcpMenuSelector: (build, _) =>
        {
            var menu = build(); var names = menu.Items.Select(item => item.Value).ToArray();
            if (menu.Title == "MCP server healthy") { Assert.Contains("logout", names); Assert.Contains("tools", names); Assert.DoesNotContain("login", names); }
            if (menu.Title == "MCP server login") { Assert.Contains("login", names); Assert.DoesNotContain("logout", names); Assert.DoesNotContain("tools", names); }
            if (menu.Title == "MCP server disabled") Assert.Equal(["enable", "enable-project"], names);
            return Task.FromResult(choices.Dequeue());
        });
        Assert.False((await menus.TryHandleAsync("/mcp")).IsError); Assert.Empty(choices);
    }
}
