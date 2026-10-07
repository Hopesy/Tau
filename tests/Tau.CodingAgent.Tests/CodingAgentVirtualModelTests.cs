// 作者：xxx
using Tau.CodingAgent.Runtime;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【路由压缩】路由到小窗口时先压缩，再使用同一决策发送，并尊重关闭和扩展取消。</summary>
    /// <param name="disabled">是否关闭自动压缩。</param><param name="cancel">是否由扩展取消压缩。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExtensionProvider_VirtualRequestCompactsForRoutedWindow(bool disabled, bool cancel)
    {
        using var fixture = new Fixture("virtual-window", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "window.js"), """
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>{
              let routes=0,summaries=0,small=0;
              const disabled=__DISABLED__,cancel=__CANCEL__;
              if(cancel)pi.on('session_before_compact',()=>({cancel:true}));
              pi.registerProvider('window-physical',{api:'window-test',baseUrl:'http://unit.test',apiKey:'physical-key',models:[
                {id:'large',contextWindow:100000,maxTokens:4000},{id:'small',contextWindow:2000,maxTokens:200}],
                streamSimple:(model,context,options)=>{
                  if(model.id==='small'){
                    small++;
                    const summarized=JSON.stringify(context.messages).includes('ROUTED SUMMARY');
                    if(summarized!==(!disabled&&!cancel))throw Error('wrong routed context '+summarized);
                  }
                  const text=routes===2&&model.id==='large'?'ROUTED SUMMARY':'physical response';
                  const input=routes===1?1800:10;
                  const stream=createAssistantMessageEventStream();
                  stream.push({type:'done',reason:'stop',message:{role:'assistant',api:model.api,provider:model.provider,model:model.id,
                    content:[{type:'text',text}],stopReason:'stop',timestamp:Date.now(),
                    usage:{input,output:1,cacheRead:0,cacheWrite:0,totalTokens:input+1,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}}});
                  return stream;
                }});
              pi.registerVirtualModel({provider:'window-router',id:'auto',name:'Auto',route:(request,ctx)=>{
                if(request.reason==='direct'){
                  summaries++;if(request.state!==undefined)throw Error('summary read state');
                  return {model:ctx.modelRegistry.find('window-physical','large'),thinkingLevel:'off'};
                }
                routes++;if(routes>2||request.reason!=='user'||(request.state?.count??0)!==routes-1)throw Error('request routed twice');
                return {model:ctx.modelRegistry.find('window-physical',routes===1?'large':'small'),thinkingLevel:'off',state:{count:routes}};
              }});
              pi.registerCommand('window-check',{handler:(_,ctx)=>{
                const states=ctx.sessionManager.getBranch().filter(entry=>entry.customType==='pi.virtual-model-state');
                if(routes!==2||small!==1||summaries!==(!disabled&&!cancel?1:0)||states.length!==2||states.at(-1).data.state.count!==2||ctx.model.provider!=='window-router')throw Error('routing counters');
              }});
            };
            """.Replace("__DISABLED__", disabled.ToString().ToLowerInvariant()).Replace("__CANCEL__", cancel.ToString().ToLowerInvariant()));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "window-router", ModelId = "auto" }, deadline.Token);
        session.Runner.CompactionSettings = new(ReserveTokens: 300, KeepRecentTokens: 0);
        ((RuntimeCodingAgentRunner)session.Runner).SetAutoCompactionEnabled(!disabled);
        await DrainAsync(session.RunAsync("old question", deadline.Token));
        var events = new List<AgentEvent>();
        await foreach (var item in session.RunAsync("new question", deadline.Token)) events.Add(item);
        Assert.Null(events.OfType<AgentEndEvent>().Last().ErrorMessage);
        if (disabled) Assert.Empty(events.OfType<CodingAgentCompactionStartEvent>());
        else
        {
            Assert.Single(events.OfType<CodingAgentCompactionStartEvent>());
            var end = Assert.Single(events.OfType<CodingAgentCompactionEndEvent>());
            Assert.Equal(cancel, end.Aborted);
            Assert.Null(end.ErrorMessage);
            Assert.True(events.IndexOf(end) < events.FindIndex(item => item is MessageEndEvent { Message: AssistantMessage }));
        }
        await DrainAsync(session.RunAsync("/window-check", deadline.Token));
    }

    /// <summary>【CodingAgent】【移除路由】重开会话时，已移除的虚拟模型回退到完整分支最后的物理响应。</summary>
    /// <param name="compact">是否先压缩，验证摘要不会替换虚拟选择或物理响应记录。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtensionProvider_RemovedVirtualModelRestoresPhysicalResponse(bool compact)
    {
        using var fixture = new Fixture("removed-virtual", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "physical.js"), """
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>pi.registerProvider('remaining-physical',{api:'restore-test',baseUrl:'http://unit.test',apiKey:'physical-key',models:[{id:'target',contextWindow:100000,maxTokens:4000}],
              streamSimple:(model,context,options)=>{
                const stream=createAssistantMessageEventStream();
                stream.push({type:'done',reason:'stop',message:{role:'assistant',api:model.api,provider:model.provider,model:model.id,
                  content:[{type:'text',text:'Persistent physical response'}],stopReason:'stop',timestamp:Date.now(),
                  usage:{input:100,output:10,cacheRead:0,cacheWrite:0,totalTokens:110,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}}});
                return stream;
              }});
            """);
        var virtualPath = Path.Combine(directory, "virtual.js");
        File.WriteAllText(virtualPath, """
            export default pi=>pi.registerVirtualModel({provider:'removed-router',id:'auto',name:'Auto',route:(request,ctx)=>({
              model:ctx.modelRegistry.find('remaining-physical','target'),thinkingLevel:'off'})});
            """);
        var path = Path.Combine(fixture.Root, "removed.jsonl");
        await using (var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, SessionPath = path,
            ProviderId = "removed-router", ModelId = "auto" }, deadline.Token))
        {
            await DrainAsync(session.RunAsync("remember this", deadline.Token));
            if (compact)
            {
                session.Runner.CompactionSettings = new(KeepRecentTokens: 0);
                var result = await session.Runner.CompactAsync(cancellationToken: deadline.Token);
                Assert.Contains("Persistent physical response", result.Summary);
            }
        }
        await using (var registered = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, SessionPath = path }, deadline.Token))
            Assert.Equal("removed-router", registered.Runner.Model.Provider);
        File.WriteAllText(virtualPath, "export default pi=>{};");
        await using var restored = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, SessionPath = path }, deadline.Token);
        Assert.Equal("remaining-physical", restored.Runner.Model.Provider);
        Assert.Equal("target", restored.Runner.Model.Id);
        await DrainAsync(restored.RunAsync("continue", deadline.Token));
        Assert.Equal("off", restored.Messages.OfType<AssistantMessage>().Last().ThinkingLevel);
    }

    /// <summary>【CodingAgent】【虚拟分支】摘要使用独立路由，切换分支恢复该分支状态及实际模型窗口。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_VirtualBranchAndSummaryKeepSeparateState()
    {
        using var fixture = new Fixture("virtual-branch", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "branch.js"), """
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>{
              let summaries=0;
              pi.registerProvider('branch-physical',{api:'branch-test',baseUrl:'http://unit.test',apiKey:'physical-key',models:[{id:'target',contextWindow:100000,maxTokens:4000}],
                streamSimple:(model,context,options)=>{
                  if(options.apiKey!=='physical-key')throw Error('request key');
                  const stream=createAssistantMessageEventStream(),message={role:'assistant',api:model.api,provider:model.provider,model:model.id,
                    content:[{type:'text',text:'A useful summary or answer'}],stopReason:'stop',timestamp:Date.now(),
                    usage:{input:100,output:10,cacheRead:0,cacheWrite:0,totalTokens:110,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}};
                  stream.push({type:'done',reason:'stop',message});return stream;
                }});
              pi.registerVirtualModel({provider:'branch-router',id:'auto',name:'Auto',route:(request,ctx)=>{
                const model=ctx.modelRegistry.find('branch-physical','target');
                if(request.reason==='direct'){
                  summaries++;if(request.state!==undefined)throw Error('direct route read branch state');
                  return {model,thinkingLevel:'off',state:{count:999}};
                }
                const current=ctx.sessionManager.getBranch().filter(entry=>entry.customType==='pi.virtual-model-state').at(-1)?.data.state.count??0;
                if((request.state?.count??0)!==current)throw Error('state from wrong branch');
                return {model,thinkingLevel:'off',state:{count:current+1}};
              }});
              pi.registerCommand('virtual-back',{handler:async(_,ctx)=>{
                const first=ctx.sessionManager.getBranch().find(entry=>entry.type==='message'&&entry.message.role==='assistant');
                const result=await ctx.navigateTree(first.id,{summarize:false});if(result.cancelled)throw Error('navigation cancelled');
              }});
              pi.registerCommand('virtual-branch-check',{handler:(_,ctx)=>{
                const states=ctx.sessionManager.getBranch().filter(entry=>entry.customType==='pi.virtual-model-state');
                const all=ctx.sessionManager.getEntries().filter(entry=>entry.customType==='pi.virtual-model-state');
                if(states.length!==2||states.at(-1).data.state.count!==2||all.length!==3||summaries!==1)throw Error('branch or summary state '+JSON.stringify({states,all,summaries}));
              }});
              pi.registerCommand('virtual-compact-check',{handler:(_,ctx)=>{
                const all=ctx.sessionManager.getEntries().filter(entry=>entry.customType==='pi.virtual-model-state');
                if(all.length!==3||all.some(entry=>entry.data.state.count===999)||summaries!==2)throw Error('compaction changed route state');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, SessionPath = Path.Combine(fixture.Root, "branch.jsonl"),
            ProviderId = "branch-router", ModelId = "auto" }, deadline.Token);
        Assert.Null(session.Runner.GetSessionStats().ContextUsage);
        await DrainAsync(session.RunAsync("first", deadline.Token));
        await DrainAsync(session.RunAsync("second", deadline.Token));
        Assert.Equal(100000, session.Runner.GetSessionStats().ContextUsage!.ContextWindow);
        var summary = await session.Runner.SummarizeBranchAsync(session.Messages, cancellationToken: deadline.Token);
        Assert.Contains("A useful summary", summary.Summary);
        await DrainAsync(session.RunAsync("/virtual-back", deadline.Token));
        await DrainAsync(session.RunAsync("forked question", deadline.Token));
        await DrainAsync(session.RunAsync("/virtual-branch-check", deadline.Token));
        session.Runner.CompactionSettings = new(KeepRecentTokens: 0);
        var compacted = await session.Runner.CompactAsync(cancellationToken: deadline.Token);
        Assert.Contains("A useful summary", compacted.Summary);
        await DrainAsync(session.RunAsync("/virtual-compact-check", deadline.Token));
        Assert.Equal("branch-router", session.Runner.Model.Provider);
    }

    /// <summary>【CodingAgent】【虚拟回合】工具续轮和失败重试保留虚拟选择，状态写入当前分支并可重新打开。</summary>
    /// <param name="persistent">是否使用持久树。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtensionProvider_VirtualAgentRoutesTurnsRetriesAndState(bool persistent)
    {
        using var fixture = new Fixture("virtual-loop", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "loop.js"), """
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>{
              let calls=0;const reasons=[];
              pi.registerTool({name:'route_ping',label:'ping',description:'test',parameters:{type:'object',properties:{}},execute:async()=>({content:[{type:'text',text:'pong'}]})});
              pi.registerProvider('physical-loop',{api:'physical-loop-api',baseUrl:'http://unit.test',apiKey:'physical-key',models:[{id:'first',reasoning:true},{id:'second',reasoning:true}],
                streamSimple:(model,context,options)=>{
                  calls++;if(options.apiKey!=='physical-key')throw Error('physical key');
                  const output=createAssistantMessageEventStream();
                  const message={role:'assistant',api:model.api,provider:model.provider,model:model.id,timestamp:Date.now(),
                    usage:{input:1,output:1,cacheRead:0,cacheWrite:0,totalTokens:2,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}},
                    content:calls===1?[{type:'toolCall',id:'ping',name:'route_ping',arguments:{}}]:[{type:'text',text:'done '+calls}],stopReason:calls===1?'toolUse':calls===2?'error':'stop'};
                  if(calls===2){message.errorMessage='stream ended before a terminal response event';output.push({type:'error',reason:'error',error:message});}
                  else output.push({type:'done',reason:message.stopReason,message});return output;
                }});
              pi.registerVirtualModel({provider:'loop-router',id:'auto',name:'Auto',route:(request,ctx)=>{
                if(ctx.model.id!=='auto'||ctx.model.provider!=='loop-router')throw Error('selection changed');
                reasons.push(request.reason);const count=(request.state?.count??0)+1;
                const expected=['user','continuation','retry','user'];
                if(request.reason!==expected[count-1])throw Error('wrong route reason '+count+': '+request.reason);
                if(count===2&&(request.previous?.model.id!=='first'||request.previous.thinkingLevel!=='low'))throw Error('missing successful response');
                if(count===3&&(request.failed?.model.id!=='first'||request.failed.thinkingLevel!=='high'||request.failed.message.thinkingLevel!=='high'||request.failed.message.stopReason!=='error'||request.messages.some(message=>message.stopReason==='error')))throw Error('failed retry context');
                return {model:ctx.modelRegistry.find('physical-loop',count>2?'second':'first'),thinkingLevel:count===2?'high':'low',state:{count}};
              }});
              pi.registerCommand('route-state',{handler:(_,ctx)=>{
                const states=ctx.sessionManager.getBranch().filter(entry=>entry.customType==='pi.virtual-model-state');
                if(states.length!==4||states.at(-1).data.state.count!==4||reasons.join(',')!=='user,continuation,retry,user')throw Error('state branch '+JSON.stringify(states));
              }});
            };
            """);
        var sessionPath = Path.Combine(fixture.Root, "virtual.jsonl");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = !persistent, SessionPath = persistent ? sessionPath : null,
            ProviderId = "loop-router", ModelId = "auto" }, deadline.Token);
        ((RuntimeCodingAgentRunner)session.Runner).RetryOptions = new(2, 0);
        var events = new List<AgentEvent>();
        await foreach (var item in session.RunAsync("first question", deadline.Token)) events.Add(item);
        Assert.Null(events.OfType<AgentEndEvent>().Last().ErrorMessage);
        Assert.Single(events.OfType<ToolExecutionEndEvent>());
        Assert.Single(events.OfType<CodingAgentAutoRetryStartEvent>());
        Assert.Equal(3, events.OfType<CodingAgentEntryAppendedEvent>().Count(item => item.Entry.TryGetProperty("customType", out var type) && type.GetString() == "pi.virtual-model-state"));
        await DrainAsync(session.RunAsync("second question", deadline.Token));
        await DrainAsync(session.RunAsync("/route-state", deadline.Token));
        Assert.Equal("loop-router", session.Runner.Model.Provider);
        Assert.Equal("second", session.Messages.OfType<AssistantMessage>().Last().Model);
        Assert.All(session.Messages.OfType<AssistantMessage>().Where(message => message.StopReason != StopReason.Error), message => Assert.Equal("low", message.ThinkingLevel));
        if (persistent)
        {
            var restored = new CodingAgentTreeSessionStore(sessionPath).LoadCurrentBranchSnapshot();
            Assert.Equal("loop-router", restored.Provider);
            Assert.Equal("auto", restored.Model);
            Assert.Equal("low", restored.Messages.OfType<AssistantMessage>().Last().ThinkingLevel);
        }
    }

    /// <summary>【CodingAgent】【虚拟直调】虚拟选择保持不变，物理请求获得自己的认证、预算及路由上下文。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_VirtualDirectStreamRoutesToPhysicalModel()
    {
        using var fixture = new Fixture("virtual-direct", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "virtual.js"), """
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>{
              let routes=0;
              pi.registerProvider('physical',{api:'physical-test',baseUrl:'http://unit.test',apiKey:'physical-key',models:[{id:'target',reasoning:true,maxTokens:11}],
                streamSimple:(model,context,options)=>{
                  if(model.provider!=='physical'||options.apiKey!=='physical-key'||options.headers?.['X-Secret']||options.env?.SECRET||options.maxTokens!==11||options.reasoning!=='low')throw Error('physical request isolation '+JSON.stringify(options));
                  if(context.messages[0].role!=='system')throw Error('system transcript lost');
                  const stream=createAssistantMessageEventStream(),message={role:'assistant',api:model.api,provider:model.provider,model:model.id,content:[{type:'text',text:'routed'}],
                    usage:{input:1,output:1,cacheRead:0,cacheWrite:0,totalTokens:2,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}},timestamp:Date.now(),stopReason:'stop'};
                  stream.push({type:'done',reason:'stop',message});return stream;
                }});
              pi.registerVirtualModel({provider:'router',id:'auto',name:'Auto',thinkingLevels:['off','high','max'],route:async(request,ctx)=>{
                routes++;
                if(request.reason!=='direct'||request.thinkingLevel!=='max'||request.state!==undefined||request.model.api!=='pi-virtual'||ctx.model.id!=='auto')throw Error('route context');
                if(request.messages[0].role!=='system'||request.messages.at(-1).content[0].text!=='question')throw Error('route transcript');
                return {model:ctx.modelRegistry.find('physical','target'),thinkingLevel:'low',state:{ignored:true}};
              }});
              pi.registerCommand('route-direct',{handler:async(_,ctx)=>{
                const registry=ctx.modelRegistry,model=registry.find('router','auto');
                pi.setThinkingLevel('max');if(pi.getThinkingLevel()!=='max')throw Error('max selection');
                pi.setThinkingLevel('minimal');if(pi.getThinkingLevel()!=='high')throw Error('explicit thinking clamp');
                pi.setThinkingLevel('max');
                if(!registry.getAvailable().some(item=>item.provider==='router')||model.contextWindow!==0||model.maxTokens!==0||model.input.join(',')!=='text,image')throw Error('virtual catalog/availability');
                if((await registry.getAvailableOfType('chat','router')).length!==1)throw Error('keyless availability');
                const context={systemPrompt:'route system',messages:[{role:'user',content:'question',timestamp:Date.now()}]};
                const result=await registry.streamSimple(model,context,{reasoning:'max',maxTokens:50,apiKey:'must-not-leak',headers:{'X-Secret':'secret'},env:{SECRET:'secret'}}).result();
                if(result.stopReason!=='stop'||result.model!=='target'||result.provider!=='physical'||ctx.model.id!=='auto'||routes!==1)throw Error('route result '+JSON.stringify(result));
                const full=await registry.complete(model,context);
                if(full.stopReason!=='error'||!full.errorMessage.includes('must be routed')||routes!==1)throw Error('API-specific virtual request must fail');
                registry.registerVirtualModel({provider:'router',id:'second',name:'Second',route:()=>({model,thinkingLevel:'off'})});
                if(registry.getModelsOfType('chat','router').length!==2)throw Error('live virtual registration');
                const invalid=await registry.streamSimple(registry.find('router','second'),context).result();
                if(invalid.stopReason!=='error'||!invalid.errorMessage.includes('not a physical model'))throw Error('recursive virtual route accepted');
                registry.unregisterVirtualModel('router','second');
                if(registry.find('router','second'))throw Error('virtual unregister');
                let collision=false;try{registry.registerVirtualModel({provider:'physical',id:'target',name:'bad',route:()=>{}});}catch(error){collision=error.message.includes('conflicts');}
                if(!collision||registry.find('physical','target').api==='pi-virtual')throw Error('physical collision');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "router", ModelId = "auto" }, deadline.Token);
        await DrainAsync(session.RunAsync("/route-direct", deadline.Token));
        Assert.Equal("router", session.Runner.Model.Provider);
        Assert.Equal("auto", session.Runner.Model.Id);
    }
}
