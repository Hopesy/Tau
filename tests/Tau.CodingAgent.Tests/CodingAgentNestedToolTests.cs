// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>递归工具复用校验、参数准备和拦截，实时更新与父子标识完整，只有父结果进入历史。</summary>
    /// <param name="persistent">是否同时验证 JSONL 保存和恢复。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedTools_PipelineEventsUsageAndPersistence(bool persistent)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              const observed=[];
              for(const type of ['tool_execution_start','tool_execution_update','tool_execution_end'])pi.on(type,e=>observed.push([type,e.toolCallId,e.parentToolCallId]));
              pi.on('tool_call',e=>{
                if(e.toolName==='blocked')return {block:true,reason:'not permitted'};
                if(e.toolName==='write'){if(e.parentToolCallId!=='root/1')throw Error('missing before parent');e.input.n++;}
              });
              pi.on('tool_result',e=>{
                if(e.toolName==='write'){if(e.parentToolCallId!=='root/1')throw Error('missing after parent');return {content:[{type:'text',text:'patched'}],structuredContent:e.structuredContent};}
              });
              pi.registerTool({name:'write',parameters:{type:'object',properties:{n:{type:'integer'},path:{type:'string'}},required:['n','path']},
                prepareArguments:a=>({...a,n:a.n===undefined?undefined:Number(a.n)}),
                execute:async(id,a,s,onUpdate)=>{
                  onUpdate({content:[{type:'text',text:'progress'}],structuredContent:{n:a.n}});
                  return {content:[{type:'text',text:'leaf'}],details:{n:a.n},structuredContent:{n:a.n},usage:{input:3,output:4,totalTokens:7,cost:{input:0.1,output:0.2}}};
                }});
              pi.registerTool({name:'middle',executionMode:'sequential',parameters:{type:'object'},execute:async(id,a,s,u,ctx)=>{
                const updates=[];
                const child=await ctx.executeTool('write',{n:'2',path:'nested.txt'},{onUpdate:p=>updates.push(p.structuredContent.n)});
                if(child.isError||child.toolCall.id!==id+'/1'||child.result.content[0].text!=='patched'||child.result.structuredContent.n!==3||updates.join()!=='3')throw Error('nested child lost data '+JSON.stringify(child));
                return {content:[],usage:{input:2,output:1,totalTokens:3}};
              }});
              pi.registerTool({name:'blocked',parameters:{type:'object'},execute:async()=>{throw Error('must not execute')}});
              pi.registerTool({name:'boom',parameters:{type:'object'},execute:async()=>{throw Error('broken'+'!'.repeat(1000))}});
              pi.registerTool({name:'root_tool',parameters:{type:'object'},execute:async(id,a,s,u,ctx)=>{
                if(!ctx.tools.some(t=>t.name==='middle')||ctx.tools.some(t=>t.name==='hidden'))throw Error('wrong callable tools');
                const good=await ctx.executeTool('middle',{});
                if(good.isError)throw Error('middle failed '+JSON.stringify(good));
                const failures=[];
                for(const [name,args] of [['missing',{}],['write',{}],['blocked',{}],['boom',{}]]){
                  const outcome=await ctx.executeTool(name,args);
                  if(!outcome.isError)throw Error('failure escaped');
                  failures.push(outcome.result.content[0].text);
                }
                return {content:[{type:'text',text:'root result'}],details:{failures},structuredContent:{secret:'program only'},usage:{input:100,output:1,totalTokens:101},terminate:true};
              }});
              pi.registerCommand('observed',{handler:()=>JSON.stringify(observed)});
            };
            """);
        var (runner, provider, commands) = CreateNestedRunner(fixture);
        using (commands)
        {
            var root = Path.GetDirectoryName(fixture.Files[0])!;
            var tree = persistent ? new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(Path.Combine(root, "nested.jsonl"), root)) : null;
            if (tree is not null) commands.BindSession(runner, tree);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var events = new List<AgentEvent>();
            await foreach (var evt in runner.RunAsync("use root", timeout.Token)) events.Add(evt);
            var parent = Assert.Single(runner.Messages.OfType<ToolResultMessage>());
            Assert.False(parent.IsError, string.Join("\n", parent.Content.OfType<TextContent>().Select(text => text.Text)));
            Assert.Single(provider.Contexts);
            Assert.Equal("root", parent.ToolCallId);
            Assert.Equal(105, parent.Usage?.InputTokens);
            Assert.Equal(6, parent.Usage?.OutputTokens);
            Assert.Equal(111, parent.Usage?.TotalTokens);
            Assert.Equal(0.3m, parent.Usage?.Cost?.Total);
            Assert.Null(parent.Usage?.ReasoningTokens);
            Assert.Null(parent.Usage?.CacheWrite1hTokens);
            var calls = Assert.IsType<NestedToolCalls>(parent.NestedCalls);
            Assert.True(calls.Complete);
            Assert.Equal(["root/1", "root/1/1", "root/2", "root/3", "root/4", "root/5"], calls.Calls.Select(call => call.Id));
            Assert.Equal("2", calls.Calls[1].Arguments!.Value.GetProperty("n").GetString());
            Assert.All(calls.Calls.Skip(2), call => Assert.Equal("error", call.Status));
            Assert.Contains("not permitted", calls.Calls[4].Error);
            Assert.Contains("broken", calls.Calls[5].Error);
            Assert.Equal(500, calls.Calls[5].Error?.Length);
            Assert.All(calls.Calls, call => Assert.True(call.DurationMs >= 0));
            var progress = Assert.Single(events.OfType<ToolExecutionUpdateEvent>());
            Assert.Equal("root/1", progress.ParentToolCallId);
            Assert.Equal(3, progress.PartialResult?.StructuredContent?.GetProperty("n").GetInt32());
            Assert.Equal(6, events.OfType<ToolExecutionStartEvent>().Count(evt => evt.ParentToolCallId is not null));
            Assert.Equal(6, events.OfType<ToolExecutionEndEvent>().Count(evt => evt.ParentToolCallId is not null));
            var end = Assert.Single(events.OfType<ToolExecutionEndEvent>(), evt => evt.ToolCallId == "root");
            Assert.Equal("program only", end.Result.StructuredContent?.GetProperty("secret").GetString());
            using var observed = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "observed", "").StatusMessage!);
            Assert.Equal(15, observed.RootElement.GetArrayLength());
            var operations = AgentCompaction.CreateFileOperations();
            AgentCompaction.ExtractFileOperationsFromMessage(parent, operations);
            Assert.Contains("nested.txt", operations.Written);
            if (tree is not null)
            {
                tree.SyncFromRunner(runner);
                var restored = Assert.Single(tree.LoadSnapshot().Messages.OfType<ToolResultMessage>());
                Assert.Equal(calls.Calls.Count, restored.NestedCalls?.Calls.Count);
                Assert.Equal(parent.Usage, restored.Usage);
                var raw = File.ReadAllText(tree.Store.Path);
                Assert.Contains("\"nestedCalls\"", raw);
                Assert.DoesNotContain("program only", raw);
            }
        }
    }

    /// <summary>子调用取消信号与父调用独立，取消进度回调保留上下文，已经取消的调用不执行工具。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task NestedTools_IndependentCancellationKeepsParentUsable()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let executed=0;
              pi.registerTool({name:'wait',parameters:{type:'object'},execute:async(id,a,signal,onUpdate)=>{
                executed++;
                onUpdate({content:[{type:'text',text:'waiting'}]});
                await new Promise(resolve=>signal.aborted?resolve():signal.addEventListener('abort',resolve,{once:true}));
                throw Error('cancelled child');
              }});
              pi.registerTool({name:'root_tool',parameters:{type:'object'},execute:async(id,a,signal,u,ctx)=>{
                const controller=new AbortController();
                const child=await ctx.executeTool('wait',{}, {signal:controller.signal,onUpdate:()=>{
                  if(ctx.isIdle())throw Error('lost callback context');
                  controller.abort();
                }});
                if(!child.isError||signal.aborted)throw Error('cancellation escaped child');
                const second=await ctx.executeTool('wait',{}, {signal:controller.signal});
                if(!second.isError||executed!==1)throw Error('already cancelled child executed');
                return {content:[{type:'text',text:'parent alive'}],terminate:true};
              }});
            };
            """);
        var (runner, _, commands) = CreateNestedRunner(fixture);
        using (commands)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await foreach (var ignored in runner.RunAsync("cancel children", timeout.Token)) { }
            var parent = Assert.Single(runner.Messages.OfType<ToolResultMessage>());
            Assert.False(parent.IsError, string.Concat(parent.Content.OfType<TextContent>().Select(text => text.Text)));
            Assert.Equal("parent alive", Assert.IsType<TextContent>(Assert.Single(parent.Content)).Text);
            Assert.Equal(2, parent.NestedCalls?.Calls.Count);
            Assert.All(parent.NestedCalls!.Calls, call => Assert.Equal("error", call.Status));
        }
    }

    /// <summary>父工具并发发起的顺序调用按开始顺序执行，持锁调用可以继续执行自己的后代。</summary>
    /// <param name="sequentialHost">是否由宿主要求全部工具顺序执行。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedTools_SequentialQueueSupportsRecursion(bool sequentialHost)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              const order=[];let active=0;
              pi.registerTool({name:'leaf',parameters:{type:'object'},execute:async()=>({content:[]})});
              pi.registerTool({name:'serial',executionMode:'sequential',parameters:{type:'object'},execute:async(id,a,s,u,ctx)=>{
                if(++active!==1)throw Error('overlapping sequential calls');
                order.push(a.n);
                const child=await ctx.executeTool('leaf',{});
                if(child.isError)throw Error('descendant failed');
                active--;return {content:[]};
              }});
              pi.registerTool({name:'root_tool',parameters:{type:'object'},execute:async(id,a,s,u,ctx)=>{
                const results=await Promise.all([1,2,3].map(n=>ctx.executeTool('serial',{n})));
                if(results.some(r=>r.isError)||order.join()!=='1,2,3')throw Error('wrong sequential order '+order);
                return {content:[],terminate:true};
              }});
            };
            """);
        var (runner, _, commands) = CreateNestedRunner(fixture, sequentialHost ? ToolExecutionMode.Sequential : ToolExecutionMode.Parallel);
        using (commands)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await foreach (var ignored in runner.RunAsync("serial children", timeout.Token)) { }
            var parent = Assert.Single(runner.Messages.OfType<ToolResultMessage>());
            Assert.False(parent.IsError, string.Concat(parent.Content.OfType<TextContent>().Select(text => text.Text)));
            Assert.Equal(6, parent.NestedCalls?.Calls.Count);
            Assert.True(parent.NestedCalls?.Complete);
        }
    }

    /// <summary>超限参数按 UTF8 字节记录大小，超限调用不再保存但用量继续累加，错误文本有界。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task NestedTools_RecordLimitsRetainAllUsage()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerTool({name:'leaf',parameters:{type:'object'},execute:async()=>({content:[],usage:{input:1,output:0,totalTokens:1}})});
              pi.registerTool({name:'root_tool',parameters:{type:'object'},execute:async(id,a,s,u,ctx)=>{
                for(let i=0;i<257;i++)await ctx.executeTool('leaf',i===0?{text:'字'.repeat(3000)}:i<7?{text:'x'.repeat(6000)}:{});
                return {content:[],terminate:true};
              }});
            };
            """);
        var (runner, _, commands) = CreateNestedRunner(fixture);
        using (commands)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
        {
            await foreach (var ignored in runner.RunAsync("many children", timeout.Token)) { }
            var parent = Assert.Single(runner.Messages.OfType<ToolResultMessage>());
            Assert.False(parent.IsError, string.Concat(parent.Content.OfType<TextContent>().Select(text => text.Text)));
            Assert.Equal(257, parent.Usage?.InputTokens);
            Assert.Equal(256, parent.NestedCalls?.Calls.Count);
            Assert.False(parent.NestedCalls?.Complete);
            Assert.Null(parent.NestedCalls!.Calls[0].Arguments);
            Assert.Equal(9011, parent.NestedCalls.Calls[0].ArgumentsBytes);
            Assert.Null(parent.NestedCalls.Calls[6].Arguments);
            Assert.Equal(6011, parent.NestedCalls.Calls[6].ArgumentsBytes);
            Assert.NotNull(parent.NestedCalls.Calls[7].Arguments);
        }
    }

    /// <summary>父调用返回时未等待的子调用以 unfinished 保存，稍后的完成不会回写已保存的父记录。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task NestedTools_UnfinishedRecordIsFrozen()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let release,child;
              pi.registerTool({name:'leaf',parameters:{type:'object'},execute:async()=>{
                await new Promise(resolve=>release=resolve);return {content:[]};
              }});
              pi.registerTool({name:'root_tool',parameters:{type:'object'},execute:async(id,a,s,u,ctx)=>{
                child=ctx.executeTool('leaf',{});
                while(!release)await new Promise(resolve=>setImmediate(resolve));
                return {content:[],terminate:true};
              }});
              pi.registerCommand('release',{handler:async()=>{release();const result=await child;if(result.isError)throw Error('child cancelled by parent completion');return 'released'}});
            };
            """);
        var (runner, _, commands) = CreateNestedRunner(fixture);
        using (commands)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await foreach (var ignored in runner.RunAsync("unawaited child", timeout.Token)) { }
            var parent = Assert.Single(runner.Messages.OfType<ToolResultMessage>());
            Assert.False(parent.NestedCalls?.Complete);
            Assert.Equal("unfinished", Assert.Single(parent.NestedCalls!.Calls).Status);
            var released = fixture.Runtime.Invoke(fixture.Files[0], "release", "", timeout.Token);
            Assert.True(released.Success, released.Error);
            Assert.Equal("unfinished", Assert.Single(parent.NestedCalls.Calls).Status);
        }
    }

    /// <summary>修改展示内容会使旧结构化结果失效，后续处理器可以提供新结果及用量。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task NestedTools_ResultHooksReplaceStructuredContentAndUsage()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerTool({name:'leaf',parameters:{type:'object'},execute:async()=>({content:[{type:'text',text:'old'}],structuredContent:{old:true},usage:{input:1,output:0,totalTokens:1}})});
              pi.registerTool({name:'root_tool',parameters:{type:'object'},execute:async(id,a,s,u,ctx)=>{
                const child=await ctx.executeTool('leaf',{});
                if(child.isError||child.result.content[0].text!=='new'||child.result.structuredContent.current!==true||child.result.usage.input!==9)throw Error('wrong transformed result '+JSON.stringify(child));
                return {content:[],terminate:true};
              }});
              pi.on('tool_result',e=>e.toolName==='leaf'?{content:[{type:'text',text:'new'}],usage:{input:9,output:0,totalTokens:9}}:undefined);
            };
            """, """
            export default pi=>pi.on('tool_result',e=>{
              if(e.toolName!=='leaf')return;
              return {structuredContent:{current:e.structuredContent===undefined&&e.usage.input===9}};
            });
            """);
        var (runner, _, commands) = CreateNestedRunner(fixture);
        using (commands)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await foreach (var ignored in runner.RunAsync("result hooks", timeout.Token)) { }
            var parent = Assert.Single(runner.Messages.OfType<ToolResultMessage>());
            Assert.False(parent.IsError, string.Concat(parent.Content.OfType<TextContent>().Select(text => text.Text)));
            Assert.Equal(9, parent.Usage?.InputTokens);
        }
    }

    /// <summary>使用真实 Node 扩展工具和合成模型创建完整工具执行环境。</summary>
    /// <param name="fixture">持久扩展进程与源文件。</param>
    /// <param name="mode">模型工具批次执行模式。</param>
    /// <param name="promptOptions">可选结构化提示配置。</param>
    /// <returns>会话运行器、模型捕获器和待释放命令存储。</returns>
    private static (RuntimeCodingAgentRunner Runner, CaptureProvider Provider, CodingAgentExtensionCommandStore Commands)
        CreateNestedRunner(Fixture fixture, ToolExecutionMode mode = ToolExecutionMode.Parallel, CodingAgentSystemPromptOptions? promptOptions = null)
    {
        var root = Path.GetDirectoryName(fixture.Files[0])!;
        var commands = new CodingAgentExtensionCommandStore(root, explicitPaths: fixture.Files, includeDefaults: false, javaScriptRuntime: fixture.Runtime);
        var provider = new CaptureProvider(new AssistantMessage([new ToolCallContent("root", "root_tool", "{}")]) { StopReason = StopReason.ToolUse });
        var registry = new ProviderRegistry(); registry.Register(provider.Api, provider);
        var model = new Model { Id = "test", Name = "Test", Provider = "synthetic", Api = provider.Api };
        var catalog = new ModelCatalog(); catalog.RegisterModel(model);
        var runner = new RuntimeCodingAgentRunner(new AgentRuntime(), new AgentLoopConfig
        {
            Model = model, ProviderRegistry = registry, Tools = commands.LoadTools(), Interceptors = commands.LoadToolInterceptors(),
            DefaultExecutionMode = mode, StreamOptions = new() { ApiKey = "synthetic" }, ConvertToLlm = AgentHarnessMessages.ConvertToLlm
        }, catalog, extensionLifecycleEventSink: fixture.Sink, workingDirectory: root, systemPromptOptions: promptOptions);
        commands.BindSession(runner);
        return (runner, provider, commands);
    }
}
