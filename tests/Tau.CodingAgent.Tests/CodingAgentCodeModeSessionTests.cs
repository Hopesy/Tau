// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【脚本 MCP 端到端】从模型脚本访问延迟发现的真实服务器，保留嵌套记录而不单独公开结果。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CodeModeMcpAutoActivationDiscoveryAndNestedCallsReachModelLoop()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); Directory.CreateDirectory(agent);
        CodingAgentMcpConfiguration.Add(Path.Combine(agent, "mcp.json"), "local", new() { ["command"] = "node", ["args"] = new JsonArray(WriteMcpSessionServer(temp.Path)) });
        var provider = new CodeModeSessionProvider("""
            const found = await searchTools('echo', {namespace:'local',limit:1.0});
            if(found.length!==1||found[0].name!=='mcp__local__echo')throw Error('discovery missing');
            const ns=await describeNamespace('local'); if(!ns.tools.includes('mcp__local__echo'))throw Error('namespace missing');
            if(!(await describeTool(found[0].name)).includes('CallToolResult'))throw Error('declaration missing');
            if(ALL_TOOLS.some(t=>t.name==='codemode'||t.name==='tool_search'))throw Error('model-only tools leaked');
            const value=await tools.mcp__local__echo({text:'你好'});
            text(JSON.parse(value.content[0].text).arguments.text);
            """);
        var registry = new ProviderRegistry(); registry.Register("sdk-prompt-capture", () => provider, "test");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = registry,
            ProviderId = "test-provider", ModelId = "test-model", EnableMcp = true, IncludeExtensions = false,
            IncludeSkills = false, IncludePromptTemplates = false, IncludeContextFiles = false
        });
        Assert.Contains("codemode", session.Runner.GetActiveToolNames());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var events = new List<AgentEvent>(); await foreach (var evt in session.RunAsync("echo", timeout.Token)) events.Add(evt);
        var result = Assert.Single(session.Messages.OfType<ToolResultMessage>());
        Assert.False(result.IsError, string.Concat(result.Content.OfType<TextContent>().Select(block => block.Text)));
        Assert.Contains("你好", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        var nested = Assert.Single(result.NestedCalls!.Calls); Assert.Equal("mcp__local__echo", nested.Name); Assert.Equal("code-1/1", nested.Id);
        Assert.Contains(events.OfType<ToolExecutionStartEvent>(), evt => evt.ParentToolCallId == "code-1");
        var declarations = Transcript.GetCurrentTools(provider.Contexts[0].Messages);
        Assert.Contains(declarations, tool => tool.Name == "codemode" && tool.ConstrainedSampling?.Type == "grammar");
        Assert.DoesNotContain(declarations, tool => tool.Name == "mcp__local__echo");
        Assert.DoesNotContain(session.Runner.GetCallableTools(), tool => tool.Name == "codemode");
    }

    /// <summary>【CodingAgent】【脚本分支端到端】验证参数准备、拦截、失败输出、状态提交、分支切换和重新打开。</summary>
    /// <param name="persistent">是否使用磁盘会话。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodeModeStateFollowsBranchesAndHooksWithOnlyMode(bool persistent)
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent"); var extensions = Path.Combine(agent, "extensions"); Directory.CreateDirectory(extensions);
        File.WriteAllText(Path.Combine(agent, "coding-agent-settings.json"), """{"codemode":{"mode":"only"}}""");
        File.WriteAllText(Path.Combine(extensions, "codemode.js"), """
            // 作者：xxx
            export default pi=>{
              pi.registerTool({name:'leaf',description:'A test leaf',parameters:{type:'object',properties:{n:{type:'integer'}},required:['n']},
                prepareArguments:a=>({...a,n:a.n===undefined?undefined:Number(a.n)}),execute:async(id,a)=>({content:[{type:'text',text:String(a.n)}]})});
              pi.registerTool({name:'blocked',parameters:{type:'object'},execute:async()=>{throw Error('must not execute')}});
              pi.on('tool_call',e=>{if(e.toolName==='blocked')return {block:true,reason:'fixture denied'};
                if(e.toolName==='leaf'){if(!e.parentToolCallId)throw Error('parent missing');e.input.n++;}});
              pi.on('tool_result',e=>{if(e.toolName==='leaf'){pi.setActiveTools(['codemode','leaf','blocked']);return {content:[{type:'text',text:'patched:'+e.content[0].text}]};}});
              pi.registerCommand('code-back',{handler:async(_,ctx)=>{
                const first=ctx.sessionManager.getBranch().find(e=>e.customType==='codemode-store');
                await ctx.navigateTree(first.id,{summarize:false});
              }});
            };
            """);
        var provider = new CodeModeSessionProvider(
            """
            if(await tools.leaf({n:'2'})!=='patched:3')throw Error('pipeline mismatch');
            try{await tools.blocked({});throw Error('block missed')}catch(e){if(!e.message.includes('fixture denied'))throw e;}
            try{await tools.leaf({});throw Error('validation missed')}catch(e){if(e.message==='validation missed')throw e;}
            store('count',1);store('remove',true);text('first');
            """,
            "store('count',2);store('remove',undefined);text('second');",
            "store('count',99);text('partial');throw Error('deliberate failure');",
            "if(load('count')!==1||load('remove')!==true)throw Error('wrong branch');store('count',3);text('branch');");
        var registry = new ProviderRegistry(); registry.Register("sdk-prompt-capture", () => provider, "test");
        var path = Path.Combine(temp.Path, "code.jsonl");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = !persistent, SessionPath = persistent ? path : null,
            ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = registry, ProviderId = "test-provider", ModelId = "test-model",
            EnableCodeMode = true, IncludeSkills = false, IncludePromptTemplates = false, IncludeContextFiles = false
        }, timeout.Token))
        {
            Assert.DoesNotContain("codemode", session.Runner.GetActiveToolNames());
            session.Runner.SetActiveTools(["codemode", "leaf", "blocked"]);
            foreach (var prompt in new[] { "first", "second", "failure", "/code-back", "branch" })
            {
                await foreach (var ignored in session.RunAsync(prompt, timeout.Token)) { }
                if (prompt is "first" or "second")
                {
                    var completed = session.Messages.OfType<ToolResultMessage>().Last();
                    Assert.False(completed.IsError, prompt + ": " + string.Concat(completed.Content.OfType<TextContent>().Select(block => block.Text)));
                }
            }
            var results = session.Messages.OfType<ToolResultMessage>().ToArray();
            Assert.False(results[^1].IsError, string.Concat(results[^1].Content.OfType<TextContent>().Select(block => block.Text)));
            Assert.Contains("branch", string.Concat(results[^1].Content.OfType<TextContent>().Select(block => block.Text)));
            var firstContext = provider.Contexts[0]; var tools = Transcript.GetCurrentTools(firstContext.Messages);
            Assert.Equal("codemode", Assert.Single(tools).Name); Assert.Contains("### `leaf`", tools[0].Description);
            Assert.Equal("codemode", Assert.Single(Transcript.GetCurrentTools(provider.Contexts[1].Messages)).Name);
            Assert.Contains("leaf", session.Runner.GetActiveToolNames());
            Assert.Contains(session.Runner.GetCallableTools(), tool => tool.Name == "leaf");
            var failed = provider.Contexts[5].Messages.OfType<ToolResultMessage>().Last(); Assert.True(failed.IsError);
            Assert.Contains("partial", string.Concat(failed.Content.OfType<TextContent>().Select(block => block.Text)));
            Assert.Contains("deliberate failure", string.Concat(failed.Content.OfType<TextContent>().Select(block => block.Text)));
            if (persistent)
            {
                var entries = File.ReadAllLines(path).Select(ParseMcpSessionJson).Where(entry => entry.TryGetProperty("customType", out var type) && type.GetString() == "codemode-store").ToArray();
                Assert.Equal(3, entries.Length);
                Assert.DoesNotContain(entries, entry => entry.GetProperty("data").GetProperty("set").GetProperty("count").GetInt32() == 99);
            }
        }
        if (!persistent) return;
        var resumedProvider = new CodeModeSessionProvider("if(load('count')!==3||load('remove')!==true)throw Error('resume state');text('restored');");
        var resumedRegistry = new ProviderRegistry(); resumedRegistry.Register("sdk-prompt-capture", () => resumedProvider, "test");
        await using var resumed = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, SessionPath = path, ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = resumedRegistry,
            ProviderId = "test-provider", ModelId = "test-model", EnableCodeMode = true, IncludeSkills = false, IncludePromptTemplates = false, IncludeContextFiles = false
        }, timeout.Token);
        Assert.Contains("codemode", resumed.Runner.GetActiveToolNames());
        await foreach (var ignored in resumed.RunAsync("resume", timeout.Token)) { }
        var last = resumed.Messages.OfType<ToolResultMessage>().Last(); Assert.False(last.IsError, string.Concat(last.Content.OfType<TextContent>().Select(block => block.Text)));
    }

    /// <summary>【CodingAgent】【脚本测试模型】每个用户回合发出一个预设脚本，工具完成后给出结束响应。</summary>
    /// <param name="scripts">按回合顺序执行的脚本。</param>
    private sealed class CodeModeSessionProvider(params string[] scripts) : IStreamProvider
    {
        internal List<LlmContext> Contexts { get; } = [];
        public string Api => "sdk-prompt-capture";
        public bool SupportsTranscriptContext => true;
        /// <summary>【CodingAgent】【脚本模型流】记录上下文并返回确定性响应。</summary><param name="model">模型。</param><param name="context">上下文。</param><param name="options">选项。</param><returns>事件流。</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) => Create(context);
        /// <summary>【CodingAgent】【脚本简化流】复用确定性响应。</summary><param name="model">模型。</param><param name="context">上下文。</param><param name="options">选项。</param><returns>事件流。</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Create(context);
        /// <summary>【CodingAgent】【脚本模型输出】交替发出脚本工具调用和完成消息。</summary><param name="context">上下文。</param><returns>已完成的响应流。</returns>
        private AssistantMessageStream Create(LlmContext context)
        {
            var index = Contexts.Count; Contexts.Add(context); var stream = new AssistantMessageStream();
            var message = index % 2 == 0 && index / 2 < scripts.Length
                ? new AssistantMessage([new ToolCallContent("code-" + (index / 2 + 1), "codemode", new JsonObject { ["code"] = scripts[index / 2] }.ToJsonString())]) { StopReason = StopReason.ToolUse }
                : new AssistantMessage([new TextContent("done")]);
            stream.Push(new DoneEvent(message)); return stream;
        }
    }
}
