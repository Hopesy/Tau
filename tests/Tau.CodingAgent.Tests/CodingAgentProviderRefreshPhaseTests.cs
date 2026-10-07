// 作者：xxx
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【刷新阶段】先恢复离线缓存，再解析在线认证；失败或缺少认证均保留缓存，成功时传入解析后的凭据。</summary>
    /// <param name="mode">认证成功、缺失、API key 失败或 OAuth 失败。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("success")]
    [InlineData("missing")]
    [InlineData("failure")]
    [InlineData("oauth-failure")]
    public async Task ExtensionProvider_RefreshRestoresOfflineStateBeforeResolvingNetworkAuthentication(string mode)
    {
        using var fixture = new Fixture("refresh-phases", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        File.WriteAllText(Path.Combine(fixture.AgentDirectory, "auth.json"), mode == "oauth-failure"
            ? """{"phases":{"type":"oauth","access":"old","refresh":"fixture","expires":1}}"""
            : """{"phases":{"type":"api_key","key":"stored","env":{"SOURCE":"stored"}}}""");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "phases.js"), """
            export default pi=>{
              const mode=__MODE__,calls=[];
              let running=false;
              const model=id=>({id,name:id,provider:'phases',api:'openai-completions',baseUrl:'https://fixture.test'});
              let models=[model('initial')];
              const auth=mode==='oauth-failure'?{oauth:{
                name:'Fixture',login:async()=>{throw Error('unexpected login');},refresh:async()=>{
                  if(models[0].id!=='cached')throw Error('OAuth resolved before cache');
                  calls.push('auth');throw Error('fixture auth failed');
                },toAuth:async credential=>({apiKey:credential.access})
              }}:{apiKey:{
                name:'Fixture',check:async()=>({type:'api_key',source:'fixture'}),
                resolve:async({credential})=>{
                  if(!running)return {auth:{apiKey:'stored'}};
                  if(models[0].id!=='cached'||credential?.key!=='stored'||credential.env?.SOURCE!=='stored')throw Error('API key resolved before cache or wrong stored credential');
                  calls.push('auth');
                  if(mode==='failure')throw Error('fixture auth failed');
                  return mode==='missing'?undefined:{auth:{apiKey:'resolved'},env:{SOURCE:'resolved'}};
                }
              }};
              pi.registerProvider({id:'phases',name:'Phases',auth,getModels:()=>models,async refreshModels(ctx){
                if(!running)return;
                if(!ctx.allowNetwork){
                  if(ctx.force!==undefined)throw Error('offline force should be undefined');
                  if(mode==='oauth-failure'?ctx.credential?.access!=='old':ctx.credential?.key!=='stored')throw Error('offline credential was resolved');
                  calls.push('offline');
                  const next=[model('cached')];
                  await ctx.publish({persist:{models:next,etag:'cached'},update:()=>{models=next;}});
                  return;
                }
                calls.push('online');
                if(mode!=='success'||ctx.force!==true||ctx.credential?.key!=='resolved'||ctx.credential.env?.SOURCE!=='resolved'||ctx.stored?.models[0].id!=='cached')throw Error('online refresh contract');
                await ctx.publish({update:()=>{models=[model('online')];}});
              }});
              pi.registerCommand('phases-check',{handler:async(_,ctx)=>{
                running=true;
                const result=await ctx.modelRegistry.refresh({providers:['phases'],allowNetwork:true,force:true});
                const failure=mode.includes('failure');
                if(failure?!result.errors.get('phases')?.message.includes('fixture auth failed'):result.errors.size!==0)throw Error('wrong phase error '+[...result.errors.values()].map(e=>e.message).join(','));
                const expected=mode==='success'?'offline,auth,online':'offline,auth';
                if(calls.join(',')!==expected)throw Error('wrong phase order '+calls.join(','));
                if(!ctx.modelRegistry.find('phases',mode==='success'?'online':'cached'))throw Error('offline catalog lost');
              }});
            };
            """.Replace("__MODE__", JsonValue.Create(mode)!.ToJsonString()));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true,
            ProviderId = "phases", ModelId = "initial"
        }, deadline.Token);
        await DrainAsync(session.RunAsync("/phases-check", deadline.Token));
        Assert.Equal(mode == "success" ? "online" : "cached", Assert.Single(session.Runner.GetModels("phases")).Id);
    }
}
