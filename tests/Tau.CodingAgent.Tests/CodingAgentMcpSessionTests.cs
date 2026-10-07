// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【MCP 资源端到端】只声明资源能力的真实服务器支持分页、模板回退、私有字段过滤与多模态读取。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpResourceToolsAggregatePagesFilterAppsAndReadMultimodalContent()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); Directory.CreateDirectory(agent);
        var script = WriteMcpSessionServer(temp.Path); var config = Path.Combine(agent, "mcp.json");
        foreach (var name in new[] { "docs", "broken", "hidden" })
            CodingAgentMcpConfiguration.Add(config, name, new() { ["command"] = "node", ["args"] = new JsonArray(script, "resources", name), ["exposure"] = name == "hidden" ? "hidden" : "direct" });
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog(), EnableMcp = true, IncludeExtensions = false });
        await session.Mcp!.WaitForServersAsync();
        Assert.All(session.Mcp.GetStatus(), status => Assert.Equal("connected", status.State));
        Assert.Empty(session.Mcp.GetTools());
        var tools = session.Runner.GetRegisteredTools().OfType<CodingAgentMcpResourceTool>().ToDictionary(tool => tool.Name);
        Assert.Equal(3, tools.Count);
        Assert.All(tools.Values, tool => { Assert.Equal("direct", tool.Exposure); Assert.True(tool.Annotations!.Value.GetProperty("readOnlyHint").GetBoolean()); Assert.Contains(tool.Name, session.Runner.GetActiveToolNames()); });
        var listing = tools["list_mcp_resources"];
        var all = (await listing.ExecuteAsync("all", ParseMcpSessionJson("{}"))).StructuredContent!.Value;
        var resources = all.GetProperty("resources").EnumerateArray().ToArray();
        Assert.Equal(2, resources.Length); Assert.All(resources, resource => Assert.Equal("docs", resource.GetProperty("server").GetString()));
        Assert.False(resources[0].TryGetProperty("_meta", out _)); Assert.False(resources[0].TryGetProperty("icons", out _));
        Assert.Equal("test://two", resources[1].GetProperty("name").GetString());
        Assert.Equal("broken", Assert.Single(all.GetProperty("errors").EnumerateArray()).GetProperty("server").GetString());
        var first = (await listing.ExecuteAsync("first", ParseMcpSessionJson("""{"server":"docs"}"""))).StructuredContent!.Value;
        Assert.Single(first.GetProperty("resources").EnumerateArray()); Assert.Equal("next", first.GetProperty("nextCursor").GetString());
        var next = (await listing.ExecuteAsync("next", ParseMcpSessionJson("""{"server":"docs","cursor":"next"}"""))).StructuredContent!.Value;
        Assert.Equal("test://two", Assert.Single(next.GetProperty("resources").EnumerateArray()).GetProperty("uri").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => listing.ExecuteAsync("cursor", ParseMcpSessionJson("""{"cursor":"next"}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => listing.ExecuteAsync("hidden", ParseMcpSessionJson("""{"server":"hidden"}""")));
        var templates = await tools["list_mcp_resource_templates"].ExecuteAsync("templates", ParseMcpSessionJson("{}"));
        Assert.Empty(templates.StructuredContent!.Value.GetProperty("resourceTemplates").EnumerateArray());
        var read = await tools["read_mcp_resource"].ExecuteAsync("read", ParseMcpSessionJson("""{"server":"docs","uri":"test://one"}"""));
        Assert.Equal(4, read.Content.Count); Assert.Equal("test://one:", Assert.IsType<TextContent>(read.Content[0]).Text);
        Assert.Equal("中文资源", Assert.IsType<TextContent>(read.Content[1]).Text); Assert.IsType<ImageContent>(read.Content[3]);
        Assert.Equal("docs", read.StructuredContent!.Value.GetProperty("server").GetString());
        Assert.False(read.StructuredContent.Value.GetProperty("contents")[0].TryGetProperty("_meta", out _));
        Assert.False(read.StructuredContent.Value.TryGetProperty("content", out _));
    }

    /// <summary>【CodingAgent】【MCP 名称稳定性】发生清理冲突时两个名称都使用摘要，列表逆序不改变工具身份。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpToolNameCollisionsRemainStableWhenServerReordersList()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); Directory.CreateDirectory(agent);
        var script = WriteMcpSessionServer(temp.Path);
        CodingAgentMcpConfiguration.Add(Path.Combine(agent, "mcp.json"), "local", new() { ["command"] = "node", ["args"] = new JsonArray(script, "collision"), ["exposure"] = "direct" });
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog(), EnableMcp = true, IncludeExtensions = false });
        await session.Mcp!.WaitForServersAsync();
        var before = session.Mcp.GetTools().ToDictionary(tool => tool.Label, tool => tool.Name);
        Assert.Equal(2, before.Count); Assert.All(before.Values, name => Assert.Matches("^mcp__local__a_b_[a-f0-9]{8}$", name));
        var version = session.Mcp.Version;
        await session.Mcp.GetTools()[0].ExecuteAsync("flip", ParseMcpSessionJson("""{"refresh":true}"""));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (session.Mcp.Version == version) await Task.Delay(10, deadline.Token);
        Assert.All(session.Mcp.GetTools(), tool => Assert.Equal(before[tool.Label], tool.Name));
    }

    /// <summary>【CodingAgent】【MCP 搜索端到端】首次仅声明搜索，命中后在下一次请求声明工具，完整加载记录持久化。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpDeferredToolSearchLoadsToolsForNextModelCallAndPersistsDeclarations()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); Directory.CreateDirectory(agent);
        var script = WriteMcpSessionServer(temp.Path);
        CodingAgentMcpConfiguration.Add(Path.Combine(agent, "mcp.json"), "local", new() { ["command"] = "node", ["args"] = new JsonArray(script), ["exposure"] = "deferred" });
        var provider = new McpSessionProvider(true); var registry = new ProviderRegistry(); registry.Register("sdk-prompt-capture", () => provider, "test");
        var sessionPath = Path.Combine(temp.Path, "session.jsonl");
        await using (var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, SessionPath = sessionPath, ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = registry,
            ProviderId = "test-provider", ModelId = "test-model", EnableMcp = true, IncludeExtensions = false,
            IncludeSkills = false, IncludePromptTemplates = false, IncludeContextFiles = false
        }))
        {
            Assert.Contains("tool_search", session.Runner.GetActiveToolNames());
            await foreach (var ignored in session.RunAsync("discover echo")) { }
            Assert.Equal(3, provider.Contexts.Count);
            Assert.DoesNotContain(Transcript.GetCurrentTools(provider.Contexts[0].Messages), tool => tool.Name == "mcp__local__echo");
            Assert.Contains(Transcript.GetCurrentTools(provider.Contexts[1].Messages), tool => tool.Name == "mcp__local__echo");
            var results = session.Messages.OfType<ToolResultMessage>().ToArray();
            Assert.Equal(2, results.Length); Assert.All(results, result => Assert.False(result.IsError));
            Assert.Contains("Loaded 1 tool.", Assert.IsType<TextContent>(Assert.Single(results[0].Content)).Text);
            Assert.DoesNotContain(session.Runner.GetCallableTools(), tool => tool.Name == "tool_search");
            session.Save();
        }
        await using var resumed = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = agent, SessionPath = sessionPath, ModelCatalog = CreateModelCatalog(), EnableMcp = true, IncludeExtensions = false });
        await resumed.Mcp!.WaitForServersAsync();
        Assert.Contains("mcp__local__echo", resumed.Runner.GetActiveToolNames());
    }

    /// <summary>【CodingAgent】【MCP 会话端到端】首轮模型看到 direct 工具并经真实 Node 服务取得结果，退出回收进程。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpSdkSessionRunsRealServerThroughModelToolLoopAndClosesProcess()
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "agent"); Directory.CreateDirectory(agent);
        var script = WriteMcpSessionServer(temp.Path);
        var config = new JsonObject { ["command"] = "node", ["args"] = new JsonArray(script), ["exposure"] = "direct",
            ["env"] = new JsonObject { ["TAU_MCP_SESSION_VALUE"] = "!printf session-value" } };
        CodingAgentMcpConfiguration.Add(Path.Combine(agent, "mcp.json"), "local", config);
        var provider = new McpSessionProvider(); var registry = new ProviderRegistry(); registry.Register("sdk-prompt-capture", () => provider, "test");
        int pid;
        await using (var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = registry,
            ProviderId = "test-provider", ModelId = "test-model",
            EnableMcp = true, IncludeExtensions = false, IncludeSkills = false, IncludePromptTemplates = false, IncludeContextFiles = false,
            Tools = ["mcp__local__echo"]
        }))
        {
            await foreach (var ignored in session.RunAsync("call MCP")) { }
            var serverStatus = Assert.Single(session.Mcp!.GetStatus());
            Assert.True(serverStatus.State == "connected", serverStatus.State + ": " + serverStatus.Error);
            Assert.Equal(["mcp__local__echo"], session.Runner.GetActiveToolNames());
            Assert.Equal(2, provider.Contexts.Count);
            Assert.Equal(["mcp__local__echo"], Transcript.GetCurrentTools(provider.Contexts[0].Messages).Select(tool => tool.Name));
            var result = Assert.Single(session.Messages.OfType<ToolResultMessage>());
            Assert.False(result.IsError, string.Join("", result.Content.OfType<TextContent>().Select(text => text.Text)));
            var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
            using var content = JsonDocument.Parse(text);
            Assert.Equal("你好", content.RootElement.GetProperty("arguments").GetProperty("text").GetString());
            Assert.Equal("session-value", content.RootElement.GetProperty("env").GetString());
            Assert.Equal(new Uri(temp.Path).AbsoluteUri, content.RootElement.GetProperty("roots")[0].GetProperty("uri").GetString());
            pid = content.RootElement.GetProperty("pid").GetInt32();
            Assert.Equal("connected", Assert.Single(session.Mcp!.GetStatus()).State);
            Assert.Equal("server instructions", Assert.IsType<CodingAgentMcpTool>(Assert.Single(session.Runner.GetRegisteredTools())).Namespace!.Value.GetProperty("instructions").GetString());
        }
        System.Diagnostics.Process? alive = null;
        try { alive = System.Diagnostics.Process.GetProcessById(pid); } catch (ArgumentException) { }
        try { Assert.True(alive is null || alive.HasExited); } finally { alive?.Dispose(); }
    }

    /// <summary>【CodingAgent】【MCP 启动限制】SDK 默认不开启 MCP，启用后禁用服务器不执行命令且失败独立记录。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpSdkOptInDisabledServersAndFailuresAreIsolated()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); Directory.CreateDirectory(agent);
        var configPath = Path.Combine(agent, "mcp.json");
        CodingAgentMcpConfiguration.Add(configPath, "disabled", new() { ["command"] = "missing-disabled-executable", ["enabled"] = false });
        CodingAgentMcpConfiguration.Add(configPath, "bad", new() { ["command"] = "missing-mcp-executable", ["exposure"] = "direct" });
        await using (var off = await CodingAgentSdk.CreateSessionAsync(new() { Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog() }))
            Assert.Null(off.Mcp);
        await using var on = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = CreatePromptCapturingRegistry(_ => { }), EnableMcp = true,
            ProviderId = "test-provider", ModelId = "test-model" });
        await foreach (var ignored in on.RunAsync("still works")) { }
        var status = on.Mcp!.GetStatus().ToDictionary(value => value.Name);
        Assert.Equal("disabled", status["disabled"].State); Assert.Equal("failed", status["bad"].State);
        Assert.NotNull(status["bad"].Error); Assert.Empty(on.Mcp.GetTools());
        Assert.Contains(on.Messages.OfType<AssistantMessage>(), message => message.Content.OfType<TextContent>().Any(text => text.Text == "ok"));
    }

    /// <summary>【CodingAgent】【MCP 动态目录】文件优先于扩展，列表通知刷新工具，手动关闭选择保留，移除配置关闭连接。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task McpSessionReconcilesConfigurationAndToolListNotifications()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); Directory.CreateDirectory(agent);
        var script = WriteMcpSessionServer(temp.Path); var configPath = Path.Combine(agent, "mcp.json");
        CodingAgentMcpConfiguration.Add(configPath, "local", new() { ["command"] = "node", ["args"] = new JsonArray(script), ["exposure"] = "direct" });
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog(), EnableMcp = true, IncludeExtensions = false });
        await session.Mcp!.WaitForServersAsync();
        var echo = Assert.Single(session.Runner.GetRegisteredTools(), tool => tool.Name == "mcp__local__echo");
        session.Runner.SetActiveTools([]);
        session.ExtensionCommandStore.McpServers.Register("local", new() { ["command"] = "missing-shadow" }, "extension");
        await session.Mcp.WaitForServersAsync();
        Assert.Equal("connected", Assert.Single(session.Mcp.GetStatus()).State);
        await echo.ExecuteAsync("refresh", ParseMcpSessionJson("""{"refresh":true}"""));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!session.Runner.GetRegisteredTools().Any(tool => tool.Name == "mcp__local__late")) await Task.Delay(10, timeout.Token);
        Assert.DoesNotContain("mcp__local__echo", session.Runner.GetActiveToolNames());
        Assert.Contains("mcp__local__late", session.Runner.GetActiveToolNames());
        CodingAgentMcpConfiguration.Remove(configPath, "local"); session.ExtensionCommandStore.McpServers.Unregister("local", "extension");
        await session.Mcp.ReloadAsync();
        Assert.Empty(session.Mcp.GetStatus());
        Assert.DoesNotContain(session.Runner.GetRegisteredTools(), tool => tool.Name.StartsWith("mcp__", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => echo.ExecuteAsync("closed", ParseMcpSessionJson("{}")));
    }

    /// <summary>【CodingAgent】【MCP 进程脚本】创建实际 JSON-RPC 服务器并支持根目录查询、工具刷新和环境回显。</summary>
    /// <param name="directory">测试目录。</param><returns>脚本路径。</returns>
    private static string WriteMcpSessionServer(string directory)
    {
        var path = Path.Combine(directory, "mcp-server.cjs");
        File.WriteAllText(path, """
            // 作者：xxx
            const readline=require('node:readline');let roots=[],late=false;const resources=process.argv[2]==='resources',collision=process.argv[2]==='collision';
            /** 【CodingAgent】【测试报文】@param {object} value 报文 @returns {void} 发送 JSON 行 */
            const send=value=>process.stdout.write(JSON.stringify(value)+'\n');
            readline.createInterface({input:process.stdin}).on('line',line=>{
              const m=JSON.parse(line),p=m.params??{};
              if(m.id==='roots'){roots=m.result?.roots??[];return;}
              const result=value=>send({jsonrpc:'2.0',id:m.id,result:value});
              if(m.method==='initialize')result({protocolVersion:p.protocolVersion,serverInfo:{name:'fixture',version:'1'},capabilities:resources?{resources:{}}:{tools:{}},instructions:'server instructions'});
              if(m.method==='notifications/initialized')send({jsonrpc:'2.0',id:'roots',method:'roots/list'});
              if(m.method==='tools/list'){
                if(resources){send({jsonrpc:'2.0',id:m.id,error:{code:-32601,message:'tools/list must not be called'}});return;}
                result({tools:(collision?(late?['a_b','a-b']:['a-b','a_b']):late?['echo','late']:['echo']).map(name=>({name,inputSchema:{type:'object'}}))});
              }
              if(m.method==='resources/list'){
                if(process.argv[3]==='broken'){send({jsonrpc:'2.0',id:m.id,error:{code:-32000,message:'listing unavailable'}});return;}
                result(p.cursor?{resources:[{uri:'test://two'},{uri:'test://app',name:'app',mimeType:'text/html; profile="mcp-app"'}]}:
                  {resources:[{uri:'test://one',name:'first',_meta:{private:true},icons:[{}]},{uri:'ui://app',name:'ui'}],nextCursor:'next'});
              }
              if(m.method==='resources/templates/list')send({jsonrpc:'2.0',id:m.id,error:{code:-32601,message:'no templates'}});
              if(m.method==='resources/read')result({contents:[{uri:p.uri,text:'中文资源',_meta:{private:true}},{uri:'test://image',blob:'aW1n',mimeType:'image/png'}]});
              if(m.method==='tools/call'){
                if(p.arguments.refresh){late=true;send({jsonrpc:'2.0',method:'notifications/tools/list_changed'});}
                result({content:[{type:'text',text:JSON.stringify({arguments:p.arguments,env:process.env.TAU_MCP_SESSION_VALUE,roots,pid:process.pid})}]});
              }
            });
            """);
        return path;
    }

    /// <summary>【CodingAgent】【MCP 测试 JSON】创建独立的测试参数。</summary><param name="json">JSON。</param><returns>独立元素。</returns>
    private static JsonElement ParseMcpSessionJson(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }

    /// <summary>【CodingAgent】【MCP 测试模型】首轮发出 MCP 工具调用，第二轮完成回答。</summary>
    private sealed class McpSessionProvider(bool searchFirst = false) : IStreamProvider
    {
        internal List<LlmContext> Contexts { get; } = [];
        public string Api => "sdk-prompt-capture";
        public bool SupportsTranscriptContext => true;
        /// <summary>【CodingAgent】【MCP 模型流】根据上下文发出工具调用或结束。</summary><param name="model">模型。</param><param name="context">上下文。</param><param name="options">选项。</param><returns>完成事件流。</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) => Create(context);
        /// <summary>【CodingAgent】【MCP 简化流】复用确定性测试输出。</summary><param name="model">模型。</param><param name="context">上下文。</param><param name="options">选项。</param><returns>完成事件流。</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Create(context);
        /// <summary>【CodingAgent】【MCP 测试输出】记录模型真实上下文并返回单条完成事件。</summary><param name="context">上下文。</param><returns>事件流。</returns>
        private AssistantMessageStream Create(LlmContext context)
        {
            Contexts.Add(context); var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(Contexts.Count == 1 && searchFirst ? new AssistantMessage([new ToolCallContent("search-call", "tool_search", "{\"query\":\"echo\",\"limit\":1.0}")]) { StopReason = StopReason.ToolUse }
                : Contexts.Count == (searchFirst ? 2 : 1) ? new AssistantMessage([new ToolCallContent("mcp-call", "mcp__local__echo", "{\"text\":\"你好\"}")]) { StopReason = StopReason.ToolUse }
                : new AssistantMessage([new TextContent("done")])));
            return stream;
        }
    }
}
