// 作者：xxx
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;
using static Tau.CodingAgent.Tests.CodingAgentMcpOAuthProtocolTests;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【MCP 提示策略】混合覆盖决定发现入口，描述优先于说明，仅展示符合条件的服务器。</summary>
    [Fact]
    public void McpPromptSelectsIndirectServersAndTheirDiscoveryRoutes()
    {
        var direct = PromptServer("direct", "direct");
        var hidden = PromptServer("hidden", "hidden");
        var disabled = PromptServer("disabled", "codemode"); disabled.Entry.Config["enabled"] = false;
        var mixed = PromptServer("alpha", "direct", "  文档说明\n不展示的第二行  ", "被覆盖的服务说明");
        mixed.Entry.Config["toolExposure"] = new JsonObject { ["one"] = "deferred", ["two"] = "codemode" };
        var deferred = PromptServer("beta", "deferred", "  ", "  服务说明\r\n第二行");
        var text = CodingAgentMcpPrompt.Render([deferred, hidden, disabled, direct, mixed]);
        Assert.Equal("MCP servers whose tools are not declared to you. Call the tools of \u0060codemode\u0060 servers from codemode scripts." +
            " Load the tools of \u0060tool_search\u0060 servers with \u0060tool_search\u0060.\n- mcp__alpha (codemode): 文档说明\n- mcp__beta (tool_search): 服务说明", text);
        Assert.Null(CodingAgentMcpPrompt.Render([direct, hidden, disabled]));
        Assert.DoesNotContain("codemode", CodingAgentMcpPrompt.Render([deferred])!);
    }

    /// <summary>【CodingAgent】【MCP 提示预算】长描述按上限截断，大量服务器省略后总正文仍满足预算。</summary>
    [Fact]
    public void McpPromptBudgetsDescriptionsAndCountsOmittedServers()
    {
        var single = CodingAgentMcpPrompt.Render([PromptServer("one", "codemode", new string('文', 300))])!;
        Assert.EndsWith(new string('文', 249) + "…", single);
        var servers = Enumerable.Range(0, 240).Select(index => PromptServer($"server{index:D3}", "codemode", new string('文', 800))).ToArray();
        var text = CodingAgentMcpPrompt.Render(servers)!;
        Assert.InRange(text.Length, 1, CodingAgentMcpPrompt.MaximumSectionCharacters);
        var kept = text.Split('\n').Count(line => line.StartsWith("- mcp__", StringComparison.Ordinal));
        Assert.InRange(kept, 1, servers.Length - 1);
        Assert.EndsWith($"- … {servers.Length - kept} more servers; find their tools with searchTools()", text);
        var hugeName = CodingAgentMcpPrompt.Render([PromptServer(new string('a', 5000), "codemode")])!;
        Assert.DoesNotContain("- mcp__", hugeName); Assert.EndsWith("- … 1 more server; find their tools with searchTools()", hugeName);
    }

    /// <summary>【CodingAgent】【MCP 提示会话】真实服务器说明进入模型上下文，配置描述动态更新，切换直接声明后删除旧目录段落。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpPromptUpdatesTranscriptWhenDescriptionsAndExposureChange()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent");
        var script = WriteMcpSessionServer(temp.Path); var path = Path.Combine(agent, "mcp.json");
        var config = new JsonObject { ["command"] = "node", ["args"] = new JsonArray(script), ["exposure"] = "deferred" };
        CodingAgentMcpConfiguration.Add(path, "local", config);
        var contexts = new List<LlmContext>();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, EnableMcp = true, IncludeExtensions = false,
            ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = CreatePromptCapturingRegistry(contexts.Add),
            ProviderId = "test-provider", ModelId = "test-model"
        });
        await session.Mcp!.WaitForServersAsync();
        await foreach (var ignored in session.RunAsync("first")) { }
        Assert.Contains("mcp__local (tool_search): server instructions", contexts[^1].SystemPrompt);
        Assert.Contains("mcp__local (tool_search): server instructions",
            Transcript.GetCurrentSystemMessage(session.Messages)!.Sections![CodingAgentMcpPrompt.SectionName]);
        config["description"] = "更新的描述\n不包含的第二行"; CodingAgentMcpConfiguration.Add(path, "local", config);
        await session.Mcp.ReloadAsync();
        await foreach (var ignored in session.RunAsync("second")) { }
        Assert.Contains("更新的描述", contexts[^1].SystemPrompt);
        Assert.DoesNotContain("server instructions", Transcript.GetCurrentSystemMessage(session.Messages)!.Sections![CodingAgentMcpPrompt.SectionName]);
        await session.Mcp.UpdateServerAsync("local", exposure: "direct");
        await foreach (var ignored in session.RunAsync("third")) { }
        Assert.DoesNotContain("<mcp_servers>", contexts[^1].SystemPrompt);
        Assert.False(Transcript.GetCurrentSystemMessage(session.Messages)!.Sections!.ContainsKey(CodingAgentMcpPrompt.SectionName));
        Assert.Contains(session.Messages.OfType<SystemMessage>(), message => message.Sections?.TryGetValue(CodingAgentMcpPrompt.SectionName, out var section) == true && section is null);
    }

    /// <summary>【MCP】【日志格式回归】验证中文、多行、结构化值、缺失数据和远程级别映射。</summary>
    [Fact]
    public void McpLogFormatsStructuredMessagesAndPreservesRemoteSeverity()
    {
        var now = new DateTimeOffset(2026, 10, 6, 12, 30, 1, 234, TimeSpan.FromHours(8));
        Assert.Equal("2026-10-06T04:30:01.234Z 【MCP】【服务器日志】 [fixture] info [remote-level=warning] worker: 中文\n    next\n",
            CodingAgentMcpServerLog.Format("fixture", ParseMcpSessionJson("""{"level":"warning","logger":"worker","data":"中文\r\nnext"}"""), now));
        Assert.EndsWith(""" error {"中文":[1,true,null]}""" + "\n",
            CodingAgentMcpServerLog.Format("fixture", ParseMcpSessionJson("""{"level":"error","data":{ "中文": [1, true, null] }}"""), now));
        Assert.EndsWith(" info undefined\n", CodingAgentMcpServerLog.Format("fixture", ParseMcpSessionJson("{}"), now));
        Assert.EndsWith(" info null\n", CodingAgentMcpServerLog.Format("fixture", ParseMcpSessionJson("null"), now));
        Assert.EndsWith(" info [1,2]\n", CodingAgentMcpServerLog.Format("fixture", ParseMcpSessionJson("[1, 2]"), now));
        Assert.Contains(@"[fixture\nforged] error [remote-level=critical]", CodingAgentMcpServerLog.Format("fixture\nforged", ParseMcpSessionJson("""{"level":"critical","data":"failure"}"""), now));
    }

    /// <summary>【MCP】【日志轮转并发】不同写入器共享追加位置，轮转替换旧备份，后续记录不被旧大小缓存误删。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpLogRotatesAndSerializesIndependentWriters()
    {
        using var temp = TempDirectory.Create(); var path = Path.Combine(temp.Path, "logs", "mcp.log");
        var logs = Enumerable.Range(0, 4).Select(_ => new CodingAgentMcpServerLog(path)).ToArray();
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        logs[0].Write("first", ParseMcpSessionJson("""{"data":"第一条"}"""));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)) stream.SetLength(CodingAgentMcpServerLog.MaximumLogBytes + 1);
        File.WriteAllText(path + ".1", "previous backup");
        await Task.WhenAll(logs.Select((log, index) => Task.Run(() =>
        {
            for (var line = 0; line < 30; line++) log.Write($"writer{index}", ParseMcpSessionJson($"{{\"data\":\"record-{index}-{line}\"}}"));
        })));
        Assert.Equal(CodingAgentMcpServerLog.MaximumLogBytes + 1, new FileInfo(path + ".1").Length);
        var lines = File.ReadAllLines(path);
        Assert.Equal(120, lines.Length); Assert.Equal(120, lines.Distinct().Count());
        Assert.All(lines, line => Assert.Contains("【MCP】【服务器日志】", line));
        Assert.StartsWith("20", File.ReadAllText(path));
    }

    /// <summary>【MCP】【日志失败隔离】目标父路径为文件时丢弃日志，不向调用链抛出异常。</summary>
    [Fact]
    public void McpLogDoesNotBreakToolsWhenDestinationIsUnwritable()
    {
        using var temp = TempDirectory.Create(); var parent = Path.Combine(temp.Path, "file"); File.WriteAllText(parent, "keep");
        var log = new CodingAgentMcpServerLog(Path.Combine(parent, "mcp.log"));
        log.Write("fixture", ParseMcpSessionJson("""{"data":"ignored"}"""));
        Assert.Equal("keep", File.ReadAllText(parent));
    }

    /// <summary>【MCP】【初始化通知】HTTP 初始化成功或协议失败前收到的日志都写到宿主指定位置。</summary>
    /// <param name="valid">初始化协议是否合法。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task McpLogReceivesNotificationsBeforeInitializationCompletes(bool valid)
    {
        using var temp = TempDirectory.Create(); var logPath = Path.Combine(temp.Path, "custom", "server.log");
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return Response(405, "");
            if (request.Method == HttpMethod.Delete) return Response(204, "");
            var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            if (!message.ContainsKey("id")) return Response(202, "");
            if (message["method"]!.GetValue<string>() != "initialize")
                return Response(200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = new JsonObject { ["tools"] = new JsonArray() } }.ToJsonString());
            var result = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = new JsonObject
            { ["protocolVersion"] = valid ? "2025-11-25" : "1900-01-01", ["capabilities"] = new JsonObject(), ["serverInfo"] = new JsonObject { ["name"] = "fixture", ["version"] = "1" } } };
            var notification = """{"jsonrpc":"2.0","method":"notifications/message","params":{"level":"info","data":"initializing 中文"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "event: message\ndata: " + notification + "\n\nevent: message\ndata: " + result.ToJsonString() + "\n\n", Encoding.UTF8, "text/event-stream") };
        }));
        var registry = new CodingAgentMcpServerRegistry(); registry.Register("fixture", new() { ["url"] = "https://mcp.test/" }, "fixture");
        await using var service = new CodingAgentMcpService(temp.Path, temp.Path, () => true, registry, new() { HttpClient = http, LogPath = logPath });
        await service.ReloadAsync(); await service.WaitForServersAsync();
        Assert.Equal(valid ? "connected" : "failed", Assert.Single(service.GetStatus()).State);
        Assert.Contains("[fixture] info initializing 中文", File.ReadAllText(logPath)); Assert.False(File.Exists(Path.Combine(temp.Path, "mcp.log")));
    }

    /// <summary>【CodingAgent】【MCP 提示样本】构造仅包含提示所需字段的服务器快照。</summary>
    /// <param name="name">服务器名。</param><param name="exposure">默认策略。</param><param name="description">配置描述。</param><param name="instructions">连接说明。</param><returns>独立样本。</returns>
    private static CodingAgentMcpPromptServer PromptServer(string name, string exposure, string? description = null, string? instructions = null) =>
        new(new(name, new JsonObject { ["command"] = "fixture", ["exposure"] = exposure, ["description"] = description }, "fixture", "extension"), instructions);
}
