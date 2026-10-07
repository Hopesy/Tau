// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【虚拟可用性更新】注销最后路由时清除临时免密状态，实体提供方的认证要求始终保留。</summary>
    /// <param name="initial">是否在初始扩展快照中已有路由。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VirtualModelLiveAvailabilityTracksItsOwnKeylessOverlay(bool initial)
    {
        using var fixture = new Fixture("virtual-availability", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "availability.js"), """
            export default pi=>{
              const provider='temporary-virtual-provider';
              const register=id=>pi.registerVirtualModel({provider,id,name:id,route:()=>{throw Error('must not route')}});
              if(__INITIAL__)register('first');
              pi.registerCommand('availability-check',{handler:(_,ctx)=>{
                const registry=ctx.modelRegistry;
                if(!__INITIAL__)register('first');
                if(!registry.hasConfiguredAuth(provider))throw Error('keyless route unavailable');
                register('second');registry.unregisterVirtualModel(provider,'first');
                if(!registry.hasConfiguredAuth(provider))throw Error('remaining route lost availability');
                registry.unregisterVirtualModel(provider,'second');
                if(registry.hasConfiguredAuth(provider))throw Error('removed route still has synthetic auth');
                if(registry.getAvailable().some(model=>model.provider===provider))throw Error('removed route still visible');
                register('first');
                pi.registerProvider(provider,{api:'fixture',baseUrl:'https://fixture.test',models:[]});
                if(registry.hasConfiguredAuth(provider))throw Error('empty physical provider received synthetic auth');
                registry.unregisterVirtualModel(provider,'first');register('first');
                if(registry.hasConfiguredAuth(provider))throw Error('new route bypassed physical auth');
                pi.registerProvider(provider,{api:'fixture',baseUrl:'https://fixture.test',apiKey:'fixture',models:[]});
                if(!registry.hasConfiguredAuth(provider))throw Error('physical credential lost');
                registry.unregisterVirtualModel(provider,'first');
                if(!registry.hasConfiguredAuth(provider))throw Error('removing route removed physical credential');
              }});
            };
            """.Replace("__INITIAL__", initial.ToString().ToLowerInvariant()));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true,
            ProviderId = "session-provider", ModelId = "test-model"
        }, deadline.Token);
        await DrainAsync(session.RunAsync("/availability-check", deadline.Token));
    }

    /// <summary>【CodingAgent】【路由状态重放】缺失状态的最新匹配条目覆盖旧值，无关类型错误不能中止有效路由。</summary>
    /// <param name="persistent">是否使用 JSONL 持久树。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VirtualModelStateReadsLatestMatchingEntryWithoutFallingBackToStaleState(bool persistent)
    {
        using var fixture = new Fixture("virtual-state-replay", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "state.js"), """
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>{
              pi.registerProvider('state-physical',{api:'state-fixture',baseUrl:'https://fixture.test',apiKey:'fixture',
                models:[{id:'target',contextWindow:10000,maxTokens:1000}],
                streamSimple:model=>{
                  const stream=createAssistantMessageEventStream();
                  stream.push({type:'done',reason:'stop',message:{role:'assistant',api:model.api,provider:model.provider,model:model.id,
                    content:[{type:'text',text:'state accepted'}],stopReason:'stop',timestamp:Date.now(),
                    usage:{input:1,output:1,cacheRead:0,cacheWrite:0,totalTokens:2,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}}});
                  return stream;
                }});
              pi.registerVirtualModel({provider:'state-router',id:'auto',name:'Auto',route:(request,ctx)=>{
                if(request.state!==undefined)throw Error('stale router state restored');
                return {model:ctx.modelRegistry.find('state-physical','target'),thinkingLevel:'off'};
              }});
              pi.registerCommand('seed-state',{handler:()=>{
                pi.appendEntry('pi.virtual-model-state',{provider:'state-router',modelId:'auto',state:{old:true}});
                pi.appendEntry('pi.virtual-model-state',{provider:'state-router',modelId:'auto'});
                pi.appendEntry('pi.virtual-model-state',{provider:42,modelId:'auto',state:{irrelevant:true}});
                pi.appendEntry('pi.virtual-model-state',{provider:'state-router',modelId:42,state:{irrelevant:true}});
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = !persistent,
            SessionPath = persistent ? Path.Combine(fixture.Root, "state.jsonl") : null,
            ProviderId = "state-router", ModelId = "auto"
        }, deadline.Token);
        await DrainAsync(session.RunAsync("/seed-state", deadline.Token));
        var events = new List<AgentEvent>();
        await foreach (var item in session.RunAsync("route", deadline.Token)) events.Add(item);
        Assert.Null(events.OfType<AgentEndEvent>().Last().ErrorMessage);
        Assert.Equal("state accepted", Assert.IsType<TextContent>(session.Messages.OfType<AssistantMessage>().Last().Content.Single()).Text);
        Assert.Empty(events.OfType<CodingAgentEntryAppendedEvent>());
    }
}
