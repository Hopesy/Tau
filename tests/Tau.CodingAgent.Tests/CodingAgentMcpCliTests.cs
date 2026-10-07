// 作者：xxx
using System.Net;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp.OAuth;
using static Tau.CodingAgent.Tests.CodingAgentMcpOAuthProtocolTests;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpCliTests
{
    /// <summary>【CodingAgent】【MCP CLI 配置】命令参数原样传递，HTTP 环境引用不提前解析，作用域提示和替换删除结果准确。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpCliAddReplaceRemovePreservesCommandArgumentsAndConfigFields()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Agent); File.WriteAllText(Path.Combine(fixture.Agent, "mcp.json"), """{"unknown":{"keep":true},"mcpServers":{}}""");
        Assert.Equal(0, await fixture.RunAsync("add", "local", "-l", "--env", "KEY=one=two", "--cwd", "work", "--", "node", "script.js", "--description", "child"));
        var local = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Cwd, ".tau", "mcp.json")))!["mcpServers"]!["local"]!;
        Assert.Equal("one=two", local["env"]!["KEY"]!.GetValue<string>());
        Assert.Equal(new[] { "script.js", "--description", "child" }, local["args"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Contains("not trusted", fixture.Output.ToString());
        Assert.Equal(0, await fixture.RunAsync("add", "remote", "--url", "https://mcp.test/", "--header", "X-Fixture=a=b", "--bearer-token-env-var", "FIXTURE_NOT_SET", "--oauth-client-secret", "$" + "{SECRET_NOT_SET}"));
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Agent, "mcp.json")))!;
        Assert.True(root["unknown"]!["keep"]!.GetValue<bool>());
        Assert.Equal("Bearer $" + "{FIXTURE_NOT_SET}", root["mcpServers"]!["remote"]!["headers"]!["Authorization"]!.GetValue<string>());
        Assert.Equal("$" + "{SECRET_NOT_SET}", root["mcpServers"]!["remote"]!["oauth"]!["clientSecret"]!.GetValue<string>());
        Assert.Equal(0, await fixture.RunAsync("add", "remote", "--url", "https://other.test/")); Assert.Contains("Replaced", fixture.Output.ToString());
        Assert.Equal(1, await fixture.RunAsync("remove", "local")); Assert.Contains("use --local", fixture.Error.ToString());
        Assert.Equal(0, await fixture.RunAsync("remove", "local", "-l")); Assert.Equal(0, await fixture.RunAsync("remove", "remote"));
        Assert.True(JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Agent, "mcp.json")))!["unknown"]!["keep"]!.GetValue<bool>());
    }

    /// <summary>【CodingAgent】【MCP CLI 参数拒绝】无效或放错传输类型的选项不能创建配置文件。</summary><param name="arguments">命令参数。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("add|bad|--url|https://mcp.test|--env|KEY=value")]
    [InlineData("add|bad|--header|KEY=value|--|node")]
    [InlineData("add|bad|--url|https://mcp.test|--header|missing")]
    [InlineData("add|bad|--url|https://mcp.test|--oauth-callback-port|0")]
    [InlineData("add|bad|--url|https://mcp.test|--oauth-callback-port|NaN")]
    [InlineData("add|bad|--url|https://mcp.test|--exposure|invalid")]
    [InlineData("add|bad|--url")]
    [InlineData("add|bad|--unknown|value")]
    [InlineData("list|extra")]
    [InlineData("logout")]
    public async Task McpCliRejectsInvalidOptionsWithoutWritingConfig(string arguments)
    {
        using var fixture = new Fixture(); Assert.Equal(1, await fixture.RunAsync(arguments.Split('|')));
        Assert.False(File.Exists(Path.Combine(fixture.Agent, "mcp.json"))); Assert.NotEmpty(fixture.Error.ToString());
    }

    /// <summary>【CodingAgent】【MCP CLI 列表】检查原工具名、资源统计、禁用项与可信项目，JSON 保持可解析且失败退出码正确。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpCliListProducesReportsAndRespectsProjectTrust()
    {
        using var fixture = new Fixture(); var calls = 0;
        fixture.Http = new(new Handler(async (request, token) =>
        {
            calls++;
            if (request.Method == HttpMethod.Get) return Response(405, "");
            if (request.Method == HttpMethod.Delete) return Response(204, "");
            var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            if (!message.ContainsKey("id")) return Response(202, "");
            var result = message["method"]!.GetValue<string>() switch
            {
                "initialize" => JsonNode.Parse("""{"protocolVersion":"2025-11-25","capabilities":{"tools":{},"resources":{}},"serverInfo":{"name":"fixture","version":"1"}}"""),
                "tools/list" => JsonNode.Parse("""{"tools":[{"name":"raw.tool","inputSchema":{"type":"object"}}]}"""),
                "resources/list" => JsonNode.Parse("""{"resources":[{"uri":"fixture://read"},{"uri":"ui://app"}]}"""),
                _ => JsonNode.Parse("""{"resourceTemplates":[]}""")
            };
            return Response(200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
        }));
        Assert.Equal(0, await fixture.RunAsync("add", "global", "--url", "https://mcp.test/"));
        Assert.Equal(0, await fixture.RunAsync("add", "project", "-l", "--", "must-not-execute"));
        fixture.Output.GetStringBuilder().Clear();
        Assert.Equal(0, await fixture.RunAsync("list", "--json"));
        var report = JsonNode.Parse(fixture.Output.ToString())!;
        Assert.Contains("not trusted", report["note"]!.GetValue<string>());
        Assert.Single(report["servers"]!.AsArray()); var server = report["servers"]![0]!;
        Assert.Equal("raw.tool", server["tools"]![0]!.GetValue<string>()); Assert.Equal(1, server["resources"]!.GetValue<int>());
        Assert.Equal(0, server["resourceTemplates"]!.GetValue<int>()); Assert.True(calls > 1);
        var projectPath = Path.Combine(fixture.Cwd, ".tau", "mcp.json");
        var local = JsonNode.Parse(File.ReadAllText(projectPath))!; local["mcpServers"]!["project"]!["enabled"] = false; File.WriteAllText(projectPath, local.ToJsonString());
        new CodingAgentProjectTrustStore(fixture.Agent).Set(fixture.Cwd, true);
        fixture.Output.GetStringBuilder().Clear(); Assert.Equal(0, await fixture.RunAsync("list", "--json"));
        report = JsonNode.Parse(fixture.Output.ToString())!;
        Assert.Null(report["note"]); Assert.Equal(2, report["servers"]!.AsArray().Count); Assert.Equal("disabled", report["servers"]![1]!["state"]!.GetValue<string>());
        File.WriteAllText(projectPath, "{broken");
        fixture.Output.GetStringBuilder().Clear(); Assert.Equal(1, await fixture.RunAsync("list", "--json"));
        Assert.NotEmpty(JsonNode.Parse(fixture.Output.ToString())!["errors"]!.AsArray());
    }

    /// <summary>【CodingAgent】【MCP 请求头优先】已有 OAuth 凭据不覆盖显式 Authorization；此类服务器不接受 MCP 登录注销。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpCliKeepsExplicitAuthorizationAndRejectsOAuthManagementForIt()
    {
        using var fixture = new Fixture(); var requests = 0;
        fixture.Http = new(new Handler((request, _) =>
        {
            requests++; Assert.Equal("fixture-manual", request.Headers.Authorization!.Parameter);
            return Task.FromResult(Response(401, ""));
        }));
        Assert.Equal(0, await fixture.RunAsync("add", "fixture", "--url", "https://mcp.test/", "--header", "aUtHoRiZaTiOn=Bearer fixture-manual"));
        var credentials = new CodingAgentMcpOAuthCredentialStore(fixture.Agent);
        await credentials.ForServer("fixture", "https://mcp.test/").SaveAsync(new() { ["serverUrl"] = "https://mcp.test/", ["tokens"] = new JsonObject { ["access_token"] = "fixture-old" } });
        Assert.Equal(1, await fixture.RunAsync("list")); Assert.Equal(1, requests);
        Assert.Equal(1, await fixture.RunAsync("login", "fixture")); Assert.Equal(1, await fixture.RunAsync("logout", "fixture"));
        Assert.Equal("fixture-old", (await credentials.TokensAsync("fixture", "https://mcp.test/"))!["access_token"]!.GetValue<string>());
        Assert.Equal(1, requests);
    }

    /// <summary>【CodingAgent】【MCP CLI 登录隔离】登录只连接选中服务器；已连接时不展示授权，注销不建立网络连接。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpCliLoginAndLogoutDoNotStartUnrelatedServers()
    {
        using var fixture = new Fixture(); var calls = 0;
        fixture.Http = new(new Handler(async (request, token) =>
        {
            calls++; Assert.Equal("mcp.test", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Get) return Response(405, "");
            var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            if (message["id"] is null) return Response(202, "");
            var result = message["method"]!.GetValue<string>() == "initialize" ? JsonNode.Parse("""{"protocolVersion":"2025-11-25","capabilities":{},"serverInfo":{"name":"fixture","version":"1"}}""") : new JsonObject();
            return Response(200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
        }));
        Assert.Equal(0, await fixture.RunAsync("add", "fixture", "--url", "https://mcp.test/"));
        Assert.Equal(0, await fixture.RunAsync("add", "unrelated", "--", "must-not-execute"));
        Assert.Equal(0, await fixture.RunAsync("login", "fixture", "--timeout", "5"));
        Assert.Contains("Already signed in", fixture.Output.ToString());
        var before = calls; Assert.Equal(0, await fixture.RunAsync("logout", "fixture")); Assert.Equal(before, calls);
        Assert.Equal(1, await fixture.RunAsync("login", "fixture", "--timeout", "0")); Assert.Equal(before, calls);
    }

    /// <summary>【CodingAgent】【MCP CLI 完整登录】通过命令完成交互、保存令牌并验证重连，注销不影响其他配置。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpCliLoginCompletesConfiguredPromptAndPersistsCredentials()
    {
        using var fixture = new Fixture(); fixture.Http = CodingAgentMcpOAuthSignInTests.ServiceHttp();
        string? callback = null;
        fixture.Prompt = new CodingAgentMcpOAuthSignInTests.Prompt((url, _) =>
        {
            var query = Form(url.Query); callback = query["redirect_uri"] + "?state=" + query["state"] + "&code=fixture";
            return Task.CompletedTask;
        }, _ => Task.FromResult(callback));
        Assert.Equal(0, await fixture.RunAsync("add", "fixture", "--url", "https://mcp.test/", "--oauth-client-id", "fixture"));
        Assert.Equal(0, await fixture.RunAsync("add", "unrelated", "--", "must-not-execute"));
        Assert.Equal(0, await fixture.RunAsync("login", "fixture", "--timeout", "5"));
        Assert.Contains("Signed in", fixture.Output.ToString()); Assert.NotNull(callback);
        var credentials = new CodingAgentMcpOAuthCredentialStore(fixture.Agent);
        Assert.Equal("fixture-login", (await credentials.TokensAsync("fixture", "https://mcp.test/"))!["access_token"]!.GetValue<string>());
        Assert.Equal(0, await fixture.RunAsync("logout", "fixture")); Assert.Null(await credentials.TokensAsync("fixture", "https://mcp.test/"));
    }

    /// <summary>【CodingAgent】【MCP CLI 超时】浏览器没有回调时取消输入、关闭监听并返回失败退出码。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpCliLoginTimeoutCancelsPromptAndReleasesPort()
    {
        using var fixture = new Fixture(); fixture.Http = CodingAgentMcpOAuthSignInTests.ServiceHttp();
        Uri? redirect = null;
        fixture.Prompt = new CodingAgentMcpOAuthSignInTests.Prompt((url, _) => { redirect = new(Form(url.Query)["redirect_uri"]); return Task.CompletedTask; },
            async token => { await Task.Delay(Timeout.Infinite, token); return null; });
        Assert.Equal(0, await fixture.RunAsync("add", "fixture", "--url", "https://mcp.test/", "--oauth-client-id", "fixture"));
        Assert.Equal(1, await fixture.RunAsync("login", "fixture", "--timeout", "1"));
        Assert.Contains("timed out", fixture.Error.ToString()); Assert.NotNull(redirect);
        await using var released = CodingAgentMcpOAuthCallbackServer.Listen(new() { Port = redirect.Port });
    }

    /// <summary>【CodingAgent】【MCP CLI 路由】帮助不创建目录，其他子命令仍由正常 CLI 处理。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task McpCliHelpAndUnrelatedCommandsHaveNoSideEffects()
    {
        using var fixture = new Fixture();
        Assert.Null(await CodingAgentMcpCli.TryHandleAsync(["--version"], fixture.Output, fixture.Error));
        Assert.Equal(0, await fixture.RunAsync("--help")); Assert.Contains("tau mcp add", fixture.Output.ToString());
        Assert.False(Directory.Exists(fixture.Agent)); Assert.Equal(1, await fixture.RunAsync("unknown"));
    }

    /// <summary>【CodingAgent】【MCP CLI 隔离目录】所有命令仅操作当前测试生成的临时目录。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tau-mcp-cli-" + Guid.NewGuid().ToString("N"));
        internal string Agent => Path.Combine(_root, "agent");
        internal string Cwd => Path.Combine(_root, "project");
        internal StringWriter Output { get; } = new();
        internal StringWriter Error { get; } = new();
        internal HttpClient? Http { get; set; }
        internal ICodingAgentMcpSignInPrompt? Prompt { get; set; }
        /// <summary>【CodingAgent】【CLI 测试创建】只创建工作目录，代理目录由被测命令按需创建。</summary>
        internal Fixture() => Directory.CreateDirectory(Cwd);
        /// <summary>【CodingAgent】【CLI 测试调用】注入隔离目录和模拟 HTTP。</summary><param name="arguments">mcp 后的参数。</param><returns>退出码。</returns>
        internal Task<int?> RunAsync(params string[] arguments) => CodingAgentMcpCli.TryHandleAsync(["mcp", .. arguments], Output, Error,
            new() { AgentDirectory = Agent, Cwd = Cwd, ServiceOptions = new() { HttpClient = Http }, SignInPrompt = Prompt });
        /// <summary>【CodingAgent】【CLI 测试清理】关闭模拟客户端并删除本测试专用目录。</summary>
        public void Dispose() { Http?.Dispose(); Output.Dispose(); Error.Dispose(); Directory.Delete(_root, true); }
    }
}
