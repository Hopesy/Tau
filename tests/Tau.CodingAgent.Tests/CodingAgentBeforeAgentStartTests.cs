// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【启动钩子】验证真实扩展处理器与结构化提示及请求投影的完整衔接。</summary>
public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>流式执行时主动发送用户消息必须明确指定队列，缺少方式不会意外增加模型请求。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task StreamingUserDelivery_RequiresQueueMode()
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('agent_start',()=>pi.sendUserMessage('invalid delivery'));
            """);
        var (runner, provider) = fixture.CreateRunner();
        await foreach (var _ in runner.RunAsync("first")) { }
        Assert.Single(provider.Contexts);
        Assert.Single(runner.Messages.OfType<UserMessage>());
    }

    /// <summary>input 接管后主动发送用户消息，保留图片和 extension 来源，默认不展开模板。</summary>
    /// <param name="expand">是否明确允许模板展开。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Input_CanReplaceTurnWithSentUserMessage(bool expand)
    {
        using var fixture = new Fixture("""
            export default pi => {
              const sources=[];
              pi.on('input', e => {
                sources.push(e.source);
                if(e.source === 'extension') return;
                pi.sendUserMessage([{type:'text',text:'/greet Ada'},{type:'image',data:'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',mimeType:'image/png'}],{expandPromptTemplates:e.text==='expand'});
                return {action:'handled'};
              });
              pi.registerCommand('sources',{handler:()=>sources.join(',')});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        var path = Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "greet.md");
        File.WriteAllText(path, "Hello $1");
        runner.ConfigureInputResources(null, new CodingAgentPromptTemplateStore(explicitPaths: [path], includeDefaults: false));
        await foreach (var _ in runner.RunAsync(expand ? "expand" : "replace")) { }
        var user = Assert.Single(Assert.Single(provider.Contexts).Messages.OfType<UserMessage>());
        Assert.Equal(expand ? "Hello Ada" : "/greet Ada", Assert.IsType<TextContent>(user.Content[0]).Text);
        Assert.Equal(new ImageContent("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=", "image/png"), user.Content[1]);
        Assert.Equal("interactive,extension", fixture.Runtime.Invoke(fixture.Files[0], "sources", "").StatusMessage);
        Assert.Single(runner.Messages.OfType<UserMessage>());
    }

    /// <summary>agent_start 主动发送的 followUp 输入进入当前运行的后续队列。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Event_SentUserMessageTriggersFollowUp()
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('agent_start',()=>pi.sendUserMessage('follow',{deliverAs:'followUp'}));
            """);
        var (runner, provider) = fixture.CreateRunner();
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("first")) events.Add(evt);
        Assert.Equal(2, provider.Contexts.Count);
        Assert.Single(events.OfType<AgentStartEvent>());
        Assert.Equal(["first", "follow"], runner.Messages.OfType<UserMessage>().Select(user => Assert.IsType<TextContent>(Assert.Single(user.Content)).Text));
    }

    /// <summary>工具执行期间仅追加的消息等工具结果写入后落盘，下一次请求能看到该消息。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AppendOnlyCustomMessage_WaitsForToolResults()
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('tool_execution_start',()=>pi.sendMessage({customType:'late',content:'after result',display:false},{triggerTurn:false}));
            """);
        var (runner, provider) = fixture.CreateRunner(new AssistantMessage([new ToolCallContent("call", "read", "{}")]) { StopReason = StopReason.ToolUse });
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("use tool")) events.Add(evt);
        var messages = runner.Messages.ToList();
        var resultIndex = messages.FindIndex(message => message is ToolResultMessage);
        var customIndex = messages.FindIndex(message => message is AgentCustomMessage);
        Assert.True(resultIndex >= 0 && customIndex > resultIndex);
        Assert.Equal(2, provider.Contexts.Count);
        Assert.Contains(provider.Contexts[1].Messages.OfType<UserMessage>(), user => user.Content.OfType<TextContent>().Any(block => block.Text == "after result"));
        Assert.Single(events.OfType<MessageStartEvent>(), evt => evt.Message is AgentCustomMessage);
        Assert.Single(events.OfType<MessageEndEvent>(), evt => evt.Message is AgentCustomMessage);
    }

    /// <summary>nextTurn 消息不会进入当前请求，只在下一次用户输入时消费。</summary>
    /// <param name="reset">是否在下一次输入前清空会话。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextTurnCustomMessage_IsBufferedAndClearedOnReset(bool reset)
    {
        using var fixture = new Fixture("""
            export default pi => { let count=0; pi.on('agent_start',()=>{
              if(++count===1) pi.sendMessage({customType:'next',content:'next only',display:false},{deliverAs:'nextTurn'});
            }); };
            """);
        var (runner, provider) = fixture.CreateRunner();
        await foreach (var _ in runner.RunAsync("first")) { }
        Assert.Empty(runner.Messages.OfType<AgentCustomMessage>());
        if (reset) runner.ResetSession();
        await foreach (var _ in runner.RunAsync("second")) { }
        Assert.Equal(reset ? 0 : 1, runner.Messages.OfType<AgentCustomMessage>().Count());
        Assert.Equal(2, provider.Contexts.Count);
    }

    /// <summary>agent_end 中发送用户消息会在上一运行完全结束后开启新运行，输入钩子只处理一次。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AgentEnd_CanStartAnotherRunWithoutReprocessingInput()
    {
        using var fixture = new Fixture("""
            export default pi => { let count=0, inputs=0;
              pi.on('input',()=>{inputs++;});
              pi.on('agent_end',()=>{if(++count===1)pi.sendUserMessage('automatic');});
              pi.registerCommand('inputs',{handler:()=>String(inputs)});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("first")) events.Add(evt);
        Assert.Equal(2, provider.Contexts.Count);
        Assert.Equal(2, events.OfType<AgentStartEvent>().Count());
        Assert.Equal(2, events.OfType<AgentEndEvent>().Count());
        Assert.Equal("2", fixture.Runtime.Invoke(fixture.Files[0], "inputs", "").StatusMessage);
    }

    /// <summary>真实 RPC 宿主将来源设置为 rpc，输入转换后再提交模型并返回成功响应。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task RpcHost_PassesSourceToInputHook()
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('input',e=>({action:'transform',text:e.source+':'+e.text}));
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var input = new StringReader("""{"id":"p","type":"prompt","message":"original"}""" + "\n");
        using var output = new StringWriter();
        var host = new CodingAgentRpcHost(runner, input, output);
        Assert.Equal(0, await host.RunAsync());
        var request = Assert.Single(provider.Contexts);
        Assert.Equal("rpc:original", Assert.IsType<TextContent>(Assert.Single(Assert.Single(request.Messages.OfType<UserMessage>()).Content)).Text);
        Assert.Contains("\"success\":true", output.ToString());
    }

    /// <summary>输入转换跨模块串行传递，省略图片保留原图，显式空数组清除图片。</summary>
    /// <param name="clearImages">第二个模块是否清除图片。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Input_TransformsAcrossModulesAndPreservesImageSemantics(bool clearImages)
    {
        using var fixture = new Fixture("""
            export default pi => {
              pi.on('input', e => { e.text='ignored'; return {action:'continue'}; });
              pi.on('input', e => ({action:'transform',text:e.text+' first'}));
              pi.on('input', () => {throw Error('isolated');});
            };
            """, """
            export default pi => pi.on('input', e => {
              if (e.source !== 'rpc' || e.streamingBehavior !== 'followUp' || e.images[0].data !== 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=') throw Error('input fields');
              return {action:'transform',text:e.text+' second', ...(e.text.startsWith('clear') ? {images:[]} : {})};
            });
            """);
        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        var result = fixture.Sink.TransformInput(clearImages ? "clear" : "keep", [new("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=", "image/png")], "rpc", "followUp", errors.Add, default);
        Assert.Equal("isolated", Assert.Single(errors).Error);
        Assert.Equal((clearImages ? "clear" : "keep") + " first second", result.Text);
        Assert.Equal(clearImages ? 0 : 1, result.Images.Count);
    }

    /// <summary>输入接管短路后续模块，不启动 Agent，也不把输入或系统更新写入历史。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Input_HandledStopsRunAndLaterModules()
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('input', () => ({action:'handled'}));
            """, """
            export default pi => {
              let calls=0; pi.on('input',()=>{calls++;});
              pi.on('before_agent_start',()=>{calls++;});
              pi.registerCommand('calls',{handler:()=>String(calls)});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        var original = runner.Messages.ToArray();
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("handled")) events.Add(evt);
        Assert.Empty(events);
        Assert.Empty(provider.Contexts);
        Assert.Equal(original, runner.Messages);
        Assert.Equal("0", fixture.Runtime.Invoke(fixture.Files[1], "calls", "").StatusMessage);
    }

    /// <summary>原始输入先进入 input，转换出的模板命令展开后才进入启动前钩子和模型请求。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Input_RunsBeforeTemplateExpansionAndBeforeAgentStart()
    {
        using var fixture = new Fixture("""
            export default pi => {
              let seen;
              pi.on('input', e => { if(e.text !== '/alias') throw Error('already expanded'); return {action:'transform',text:'/greet Ada'}; });
              pi.on('before_agent_start', e => {seen=e.prompt;});
              pi.registerCommand('seen',{handler:()=>seen});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        var path = Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "greet.md");
        File.WriteAllText(path, "Hello $1");
        runner.ConfigureInputResources(null, new CodingAgentPromptTemplateStore(explicitPaths: [path], includeDefaults: false));
        await foreach (var _ in runner.RunAsync("/alias")) { }
        Assert.Equal("Hello Ada", fixture.Runtime.Invoke(fixture.Files[0], "seen", "").StatusMessage);
        Assert.Equal("Hello Ada", Assert.IsType<TextContent>(Assert.Single(Assert.Single(provider.Contexts).Messages.OfType<UserMessage>().Single().Content)).Text);
    }

    /// <summary>【CodingAgent】【空闲排队】排队入口只转换一次，空闲时传递来源但省略 streamingBehavior。</summary>
    /// <param name="followUp">是否使用后续队列。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Input_IdleQueueIncludesSourceAndOmitsStreamingBehavior(bool followUp)
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('input', e => e.text==='drop' ? {action:'handled'} : {action:'transform',text:e.source+':'+e.streamingBehavior+':'+e.text});
            """);
        var (runner, _) = fixture.CreateRunner();
        runner.ConfigureInputResources(null, null, "rpc");
        if (followUp) { runner.FollowUp("value"); runner.FollowUp("drop"); }
        else { runner.Steer("value"); runner.Steer("drop"); }
        Assert.Equal(1, runner.PendingMessageCount);
        var queued = runner.DrainQueuedMessages();
        Assert.Equal("rpc:undefined:value", Assert.Single(followUp ? queued.FollowUp : queued.Steering));
    }

    /// <summary>无效输入变换报告错误并继续后续处理器，原始文本不会丢失。</summary>
    [Fact]
    public void Input_InvalidTransformIsIsolated()
    {
        using var fixture = new Fixture("""
            export default pi => {
              pi.on('input',()=>({action:'transform',text:42}));
              pi.on('input',e=>({action:'transform',text:e.text+' valid'}));
            };
            """);
        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        var result = fixture.Sink.TransformInput("original", [], "interactive", null, errors.Add, default);
        Assert.Single(errors);
        Assert.Equal("original valid", result.Text);
    }

    /// <summary>JavaScript getter 与宿主对所有段落的渲染一致，修改选项后 getter 立即变化。</summary>
    /// <param name="custom">是否使用自定义前言。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Getter_MatchesHostRenderingAndSeesInPlaceChanges(bool custom)
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('before_agent_start', (e,ctx) => {
              const initial = e.systemPrompt;
              e.systemPromptOptions.sections.extra = 'Changed';
              e.systemPromptOptions.promptGuidelines.push(' Added rule ');
              if (e.systemPrompt !== ctx.getSystemPrompt()) throw Error('inconsistent getter');
              return {message:{customType:'rendered',content:e.systemPrompt,display:false,details:{initial}}};
            });
            """);
        var options = new CodingAgentSystemPromptOptions
        {
            Cwd = @"C:\example", CustomPrompt = custom ? "Custom" : null, SelectedTools = ["read", "bash", "powershell"],
            ToolSnippets = new() { ["read"] = "Read", ["bash"] = "Run" },
            ToolGuidelines = new() { ["read"] = ["Rule", "Rule"] },
            AppendSystemPrompt = "Append", ContextFiles = [new(@"C:\example\AGENTS.md", "Project")],
            Skills = [new("<&", "Quote'", "", "/skill/SKILL.md", "/skill", "test", false)]
        };
        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        var result = fixture.Sink.BeforeAgentStart("input", [], options, errors.Add, default);
        Assert.Empty(errors);
        var message = Assert.IsType<AgentCustomMessage>(Assert.Single(result.Messages));
        Assert.Equal(CodingAgentSystemPrompt.Build(options), Assert.IsType<JsonElement>(message.Details).GetProperty("initial").GetString());
        Assert.Equal(CodingAgentSystemPrompt.Build(result.Options), Assert.IsType<TextContent>(Assert.Single(message.Content)).Text);
        Assert.Empty(options.Sections);
        Assert.Empty(options.PromptGuidelines);
    }

    /// <summary>多个模块共享前一个模块修改后的配置，抛错处理器之后仍继续执行后续处理器。</summary>
    [Fact]
    public void Modules_ChainOptionsAndRecoverFromHandlerError()
    {
        using var fixture = new Fixture("""
            export default pi => {
              pi.on('before_agent_start', e => { e.systemPromptOptions.sections.first='First'; throw Error('expected failure'); });
              pi.on('before_agent_start', e => ({systemPrompt:e.systemPrompt+'\nForced'}));
            };
            """, """
            export default pi => pi.on('before_agent_start', e => {
              if (!e.systemPrompt.endsWith('Forced') || e.systemPromptOptions.sections.first !== 'First') throw Error('broken chain');
              return {message:{customType:'state',content:e.prompt+':'+e.images[0].mimeType,display:true},systemPrompt:''};
            });
            """);
        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        var result = fixture.Sink.BeforeAgentStart("question", [new("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=", "image/png")], new() { Cwd = "/cwd" }, errors.Add, default);
        Assert.Equal("expected failure", Assert.Single(errors).Error);
        Assert.Equal("", result.Options.ForceSystemPrompt);
        Assert.Equal("First", result.Options.Sections["first"]);
        Assert.Equal("question:image/png", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<AgentCustomMessage>(Assert.Single(result.Messages)).Content)).Text);
    }

    /// <summary>强制提示只发送给模型，段落修改和注入消息会记录；下一轮恢复基础配置并输出删除增量。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ForcedPrompt_IsRequestOnlyAndResetsNextRun()
    {
        using var fixture = new Fixture("""
            export default pi => {
              let count=0; const seen=[];
              pi.on('before_agent_start', e => {
                if (++count !== 1) return;
                e.systemPromptOptions.sections.transient='One run';
                return {systemPrompt:'FORCED ONLY',message:{customType:'injected',content:'extra context',details:{marker:1},display:false}};
              });
              pi.on('context_with_system', e => { e.messages.push({role:'system',content:'context-added',timestamp:0}); });
              pi.on('agent_start', (_,ctx) => seen.push(ctx.getSystemPrompt()));
              pi.registerCommand('seen',{handler:()=>JSON.stringify(seen)});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        await foreach (var _ in runner.RunAsync("first")) { }
        Assert.Equal("FORCED ONLY", Transcript.GetCurrentSystemPrompt(provider.Contexts[0].Messages));
        Assert.Single(provider.Contexts[0].Messages.OfType<SystemMessage>());
        Assert.Equal("read", Assert.Single(Transcript.GetCurrentTools(provider.Contexts[0].Messages)).Name);
        var injected = Assert.Single(runner.Messages.OfType<AgentCustomMessage>());
        Assert.False(injected.Display);
        Assert.Equal(1, Assert.IsType<JsonElement>(injected.Details).GetProperty("marker").GetInt32());
        Assert.DoesNotContain("FORCED ONLY", Transcript.GetCurrentSystemPrompt(runner.Messages));
        Assert.Contains("One run", Transcript.GetCurrentSystemPrompt(runner.Messages));
        Assert.Contains(provider.Contexts[0].Messages.OfType<UserMessage>(), message => message.Content.OfType<TextContent>().Any(block => block.Text == "extra context"));
        await foreach (var _ in runner.RunAsync("second")) { }
        Assert.DoesNotContain("FORCED ONLY", Transcript.GetCurrentSystemPrompt(provider.Contexts[1].Messages));
        Assert.DoesNotContain("One run", Transcript.GetCurrentSystemPrompt(provider.Contexts[1].Messages));
        Assert.Contains(runner.Messages.OfType<SystemMessage>(), message => message.Sections is { } sections && sections.TryGetValue("transient", out var value) && value is null);
        using var seen = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "seen", "").StatusMessage!);
        Assert.Equal("FORCED ONLY", seen.RootElement[0].GetString());
        Assert.DoesNotContain("FORCED ONLY", seen.RootElement[1].GetString());
    }

    /// <summary>选择工具的显式修改优先于同步控制，未显式修改时保留处理器设置的活动集合。</summary>
    /// <param name="explicitSelection">是否直接修改选项中的工具名称。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedTools_RespectExplicitEditPrecedence(bool explicitSelection)
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('before_agent_start', e => {
              pi.setActiveTools(['other']);
              if (e.prompt === 'explicit') e.systemPromptOptions.selectedTools=['read','missing','read'];
            });
            """);
        var (runner, provider) = fixture.CreateRunner();
        await foreach (var _ in runner.RunAsync(explicitSelection ? "explicit" : "live")) { }
        var expected = explicitSelection ? "read" : "other";
        Assert.Equal([expected], runner.GetActiveToolNames());
        Assert.Equal(expected, Assert.Single(Transcript.GetCurrentTools(Assert.Single(provider.Contexts).Messages)).Name);
    }

    /// <summary>不合法段落修改只产生诊断，原始会话仍可继续执行。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task InvalidOptions_AreIsolatedFromRun()
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('before_agent_start', e => { e.systemPromptOptions.sections['bad tag']='bad'; });
            """);
        var (runner, provider) = fixture.CreateRunner();
        await foreach (var _ in runner.RunAsync("input")) { }
        Assert.Single(provider.Contexts);
        Assert.DoesNotContain("bad tag", runner.GetSystemPrompt());
    }

    /// <summary>隔离扩展文件、运行时和模型捕获器。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tau-before-start-" + Guid.NewGuid().ToString("N"));
        public CodingAgentJavaScriptExtensionRuntime Runtime { get; }
        public CodingAgentExtensionLifecycleEventSink Sink { get; }
        public List<string> Files { get; } = [];

        /// <summary>加载多个保持独立闭包的扩展模块。</summary>
        /// <param name="sources">模块源代码。</param>
        public Fixture(params string[] sources)
        {
            Directory.CreateDirectory(_root);
            Runtime = new(_root);
            var modules = new List<CodingAgentExtensionLifecycleEventModule>();
            foreach (var source in sources)
            {
                var file = Path.Combine(_root, Files.Count + ".js");
                File.WriteAllText(file, source);
                Files.Add(file);
                var loaded = Runtime.Load(file);
                Assert.True(loaded.Success, loaded.Error);
                Assert.Equal(0, loaded.Unsupported.Handlers);
                modules.Add(new(file, "test", "javascript", loaded.EventHandlerTypes));
            }
            Sink = new(modules, Runtime);
        }

        /// <summary>创建捕获真实 Agent 请求的运行器，并绑定扩展控制。</summary>
        /// <param name="responses">按调用顺序返回的可选响应，耗尽后返回固定结束回复。</param>
        /// <returns>运行器和捕获提供方。</returns>
        public (RuntimeCodingAgentRunner, CaptureProvider) CreateRunner(params AssistantMessage[] responses) => CreateRunnerWithWindow(null, responses);

        /// <summary>【CodingAgent】【测试认证】显式测试密钥；空值用于缺少认证的预检回归。</summary>
        public string? RequestApiKey { get; set; } = "synthetic";

        /// <summary>【CodingAgent】【模型目录夹具】在创建运行器前配置额外模型及能力。</summary>
        public Action<ModelCatalog>? ConfigureModelCatalog { get; set; }

        /// <summary>创建具有指定上下文窗口的真实运行器。</summary>
        /// <param name="contextWindow">模型窗口，空值表示未知。</param>
        /// <param name="responses">顺序响应列表。</param>
        /// <returns>运行器和捕获提供方。</returns>
        public (RuntimeCodingAgentRunner, CaptureProvider) CreateRunnerWithWindow(int? contextWindow, params AssistantMessage[] responses)
            => CreateRunnerWithLimits(contextWindow, null, responses);

        /// <summary>创建具有指定上下文与输出窗口的真实运行器。</summary>
        /// <param name="contextWindow">上下文窗口。</param>
        /// <param name="maxOutput">原始最大输出预算。</param>
        /// <param name="responses">顺序响应列表。</param>
        /// <returns>运行器和捕获提供方。</returns>
        public (RuntimeCodingAgentRunner, CaptureProvider) CreateRunnerWithLimits(int? contextWindow, int? maxOutput, params AssistantMessage[] responses)
        {
            var provider = new CaptureProvider(responses);
            var registry = new ProviderRegistry();
            registry.Register(provider.Api, provider);
            var model = new Model { Id = "test", Name = "Test", Provider = "synthetic", Api = provider.Api, ContextWindow = contextWindow, MaxOutputTokens = maxOutput };
            var catalog = new ModelCatalog();
            catalog.RegisterModel(model);
            ConfigureModelCatalog?.Invoke(catalog);
            model = catalog.GetModel(model.Provider, model.Id);
            var runner = new RuntimeCodingAgentRunner(new AgentRuntime(), new AgentLoopConfig
            {
                Model = model, Tools = [new StubTool("read"), new StubTool("other")], ProviderRegistry = registry,
                ConvertToLlm = AgentHarnessMessages.ConvertToLlm, StreamOptions = new() { ApiKey = RequestApiKey }
            }, catalog, extensionLifecycleEventSink: Sink, workingDirectory: _root,
                systemPromptOptions: new() { Cwd = _root, CustomPrompt = "Base", SelectedTools = ["read", "other"] });
            runner.SetActiveTools(["read"]);
            Runtime.BindSession(runner);
            return (runner, provider);
        }

        /// <summary>绑定实际命令存储，与 SDK、RPC 和交互宿主使用相同路由。</summary>
        /// <param name="runner">本测试运行器。</param>
        /// <returns>拥有当前扩展运行时的命令存储。</returns>
        public CodingAgentExtensionCommandStore BindCommands(RuntimeCodingAgentRunner runner)
        {
            var commands = new CodingAgentExtensionCommandStore(_root, explicitPaths: Files, includeDefaults: false, javaScriptRuntime: Runtime);
            commands.BindSession(runner);
            return commands;
        }

        /// <summary>关闭 Node 并清理本测试创建的文件。</summary>
        public void Dispose()
        {
            Runtime.Dispose();
            Directory.Delete(_root, true);
        }
    }

    private sealed class StubTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description => "Test tool";
        public JsonElement ParameterSchema { get; } = CreateSchema();
        /// <summary>创建不依赖反射的独立参数 Schema。</summary>
        /// <returns>空对象参数定义。</returns>
        private static JsonElement CreateSchema()
        {
            using var document = JsonDocument.Parse("""{"type":"object"}""");
            return document.RootElement.Clone();
        }
        /// <summary>返回空结果，测试仅验证工具声明。</summary>
        /// <param name="toolCallId">调用标识。</param>
        /// <param name="args">工具参数。</param>
        /// <param name="ct">取消信号。</param>
        /// <param name="onUpdate">进度回调。</param>
        /// <returns>空工具结果。</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null) => Task.FromResult(new ToolResult([]));
    }

    private sealed class CaptureProvider(params AssistantMessage[] responses) : IStreamProvider
    {
        public string Api => "test-before-start";
        public bool SupportsTranscriptContext => true;
        public List<LlmContext> Contexts { get; } = [];
        public Func<StreamOptions, AssistantMessageStream>? StreamFactory { get; set; }
        /// <summary>捕获请求并返回固定回复。</summary>
        /// <param name="model">目标模型。</param>
        /// <param name="context">真实模型上下文。</param>
        /// <param name="options">请求选项。</param>
        /// <returns>已完成的响应流。</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            Contexts.Add(context);
            if (StreamFactory is { } factory) return factory(options);
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(Contexts.Count <= responses.Length ? responses[Contexts.Count - 1] :
                new AssistantMessage([new TextContent("done")]) { StopReason = StopReason.EndTurn }));
            return stream;
        }
        /// <summary>复用捕获路径处理简单请求。</summary>
        /// <param name="model">目标模型。</param>
        /// <param name="context">上下文。</param>
        /// <param name="options">简单请求选项。</param>
        /// <returns>响应流。</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Stream(model, context, options);
    }
}
