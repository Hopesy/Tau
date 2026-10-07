// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【异步可用性】认证检查独立于请求解析，跨能力过滤、错误和取消均保持原生行为。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_AvailabilityRunsFreshChecksAndFilters()
    {
        using var fixture = new Fixture("availability", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "availability.js"), """
            export default pi=>{
              let checks=0,resolves=0,mode='ok';
              const models=['chat','image','classifier'].flatMap(type=>['yes','no'].map(id=>({type,id,provider:'available',api:'custom',baseUrl:'http://unit.test'})));
              const provider={id:'available',getModels:()=>models.filter(model=>model.type==='chat'),getAllModels:()=>models,
                auth:{apiKey:{name:'test',resolve:async()=>{resolves++;return {auth:{apiKey:'key'}};},check:async({signal})=>{
                  checks++;
                  if(mode==='error')throw Error('check failure');
                  if(mode==='wait')await new Promise((resolve,reject)=>signal.addEventListener('abort',()=>reject(Error('check aborted')),{once:true}));
                  return mode==='none'?undefined:{type:'api_key',source:'check'};
                }}},
                filterModels:models=>models.filter(model=>model.id==='yes'),filterAllModels:models=>models.filter(model=>model.id==='yes')};
              pi.registerProvider(provider);
              pi.registerCommand('available-check',{handler:async(_,ctx)=>{
                const registry=ctx.modelRegistry,start=checks;
                for(const type of ['chat','image','classifier']){
                  const models=await registry.getAvailableOfType(type,'available');
                  if(models.length!==1||models[0].id!=='yes'||(models[0].type??'chat')!==type)throw Error('type filter '+type+' '+JSON.stringify(models));
                }
                if(checks!==start+3||resolves!==0)throw Error('must run fresh check without resolve');
                if(registry.getAvailable().filter(model=>model.provider==='available').length!==1)throw Error('sync chat filter');
                delete provider.filterAllModels;
                if((await registry.getAvailableOfType('image','available')).length!==2||(await registry.getAvailableOfType('chat','available')).length!==1)throw Error('legacy chat filter removed other capabilities');
                mode='none';if((await registry.getAvailableOfType('image','available')).length)throw Error('check not refreshed');
                mode='error';let failed=false;try{await registry.getAvailableOfType('image','available');}catch(error){failed=error.message.includes('check failure');}
                if(!failed)throw Error('check error swallowed');
                mode='wait';const controller=new AbortController();const pending=registry.getAvailableOfType('image','available',{signal:controller.signal});
                setTimeout(()=>controller.abort(),60);failed=false;try{await pending;}catch{failed=true;}
                if(!failed)throw Error('abort did not reject');mode='ok';
                if((await registry.getAvailableOfType('classifier','available')).length!==2)throw Error('query after abort failed');
                if((await registry.getAvailableOfType('image','unknown')).length)throw Error('unknown provider');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" }, deadline.Token);
        await DrainAsync(session.RunAsync("/available-check", deadline.Token));
    }

    /// <summary>【CodingAgent】【OAuth 可用性】过期的存储凭据用于模型过滤，查询不得触发刷新或 API key 检查。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_AvailabilityUsesStoredOAuthWithoutRefresh()
    {
        using var fixture = new Fixture("availability-oauth", "models");
        File.WriteAllText(Path.Combine(fixture.AgentDirectory, "auth.json"), """{"available-oauth":{"type":"oauth","access":"old","refresh":"refresh","expires":1,"tier":"paid"}}""");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "oauth.js"), """
            export default pi=>{
              const model={id:'paid',type:'image',provider:'available-oauth',api:'custom',baseUrl:'http://unit.test'};
              let refreshes=0,checks=0;
              pi.registerProvider({id:'available-oauth',getModels:()=>[],getAllModels:()=>[model],
                auth:{oauth:{name:'OAuth',login:async()=>{},refresh:async()=>{refreshes++;throw Error('must not refresh');},toAuth:async()=>({apiKey:'unused'})},
                  apiKey:{name:'key',resolve:async()=>{checks++;throw Error('must not resolve');},check:async()=>{checks++;throw Error('must not check');}}},
                filterAllModels:(models,credential)=>{if(credential?.type!=='oauth'||credential.tier!=='paid')throw Error('raw credential lost');return models;}});
              pi.registerCommand('available-oauth',{handler:async(_,ctx)=>{
                const start=checks;if(start!==0||ctx.modelRegistry.getError())throw Error('OAuth bootstrap checked API key');
                const models=await ctx.modelRegistry.getAvailableOfType('image','available-oauth');
                if(models.length!==1||models[0].id!=='paid'||refreshes||checks!==start)throw Error('OAuth availability changed credentials');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        await DrainAsync(session.RunAsync("/available-oauth"));
    }
}
