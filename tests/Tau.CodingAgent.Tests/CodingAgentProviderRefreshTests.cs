// 作者：xxx
using System.Text.Json;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【刷新隔离】较早的刷新在较新刷新或重新注册后完成，不得污染目录及磁盘缓存。</summary>
    /// <param name="replace">是否使用重新注册代替第二次刷新。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtensionProvider_StaleRefreshCannotPublishAfterNewRefreshOrRegistration(bool replace)
    {
        using var fixture = new Fixture("refresh-generation", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "race.js"), """
            export default pi=>{
              let started,release;
              const model=id=>({id,api:'openai-completions',baseUrl:'https://race.invalid'});
              pi.registerProvider('race',{api:'openai-completions',baseUrl:'https://race.invalid',apiKey:'test',models:[model('initial')],
                async refreshModels(ctx){
                  if(!ctx.allowNetwork)return [model('initial')];
                  const id=ctx.force?'fast':'slow';
                  if(!ctx.force){started();await new Promise(resolve=>{release=resolve;});}
                  await ctx.publish({persist:{models:[model(id)],etag:id}});
                  return [model(id)];
                }
              });
              pi.registerCommand('race',{handler:async(arg,ctx)=>{
                const ready=new Promise(resolve=>{started=resolve;});
                const first=ctx.modelRegistry.refresh({allowNetwork:true,force:false,providers:['race']});
                await ready;
                if(arg==='replace'){
                  pi.unregisterProvider('race');
                  pi.registerProvider('race',{api:'openai-completions',baseUrl:'https://race.invalid',models:[model('replacement')]});
                }
                else await ctx.modelRegistry.refresh({allowNetwork:true,force:true,providers:['race']});
                release();await first;
                if(ctx.modelRegistry.find('race','slow'))throw Error('stale catalog published');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "race" });
        await DrainAsync(session.RunAsync(replace ? "/race replace" : "/race"));
        Assert.Equal(replace ? "replacement" : "fast", Assert.Single(session.Runner.GetModels("race")).Id);
        var cache = Path.Combine(fixture.AgentDirectory, "models-store.json");
        if (replace) Assert.False(File.Exists(cache));
        else Assert.DoesNotContain("slow", File.ReadAllText(cache));
    }

    /// <summary>【CodingAgent】【刷新取消】共享取消信号到达回调，取消后的发布不能修改目录或写入缓存。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_CancelledRefreshReturnsAbortedAndRejectsPublication()
    {
        using var fixture = new Fixture("refresh-cancel", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cancel.js"), """
            export default pi=>{
              let started;
              pi.registerProvider('cancel-refresh',{api:'openai-completions',baseUrl:'https://cancel.invalid',apiKey:'test',models:[{id:'initial'}],
                async refreshModels(ctx){
                  if(!ctx.allowNetwork)return [{id:'initial'}];
                  started();
                  await new Promise(resolve=>{if(ctx.signal.aborted)resolve();else ctx.signal.addEventListener('abort',resolve,{once:true});});
                  if(await ctx.publish({persist:{models:[]}}))throw Error('cancelled publication accepted');
                  return [{id:'invalid'}];
                }
              });
              pi.registerCommand('cancel-refresh',{handler:async(_,ctx)=>{
                const ready=new Promise(resolve=>{started=resolve;});
                const controller=new AbortController();
                const pending=ctx.modelRegistry.refresh({allowNetwork:true,signal:controller.signal});
                await ready;controller.abort();
                const result=await pending;if(!result.aborted)throw Error('missing aborted flag');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "cancel-refresh" });
        await DrainAsync(session.RunAsync("/cancel-refresh"));
        Assert.Equal("initial", Assert.Single(session.Runner.GetModels("cancel-refresh")).Id);
        Assert.False(File.Exists(Path.Combine(fixture.AgentDirectory, "models-store.json")));
    }

    /// <summary>【CodingAgent】【目录刷新】启动前离线发现模型，显式刷新传递认证与网络选项并重读文件，失败保留已发布目录。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_RefreshesBeforeSelectionAndPreservesCatalogOnFailure()
    {
        using var fixture = new Fixture("provider-refresh", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "refresh.js"), """
            export default pi=>{
              pi.registerProvider('refresh',{api:'openai-completions',baseUrl:'https://refresh.invalid',apiKey:'refresh-key',models:[],
                async refreshModels(context){
                  if(context.credential?.key!=='refresh-key')throw Error('missing refresh auth');
                  if(context.allowNetwork && !context.force)throw Error('expected refresh failure');
                  return [{id:context.allowNetwork?'online':'offline',name:'Dynamic'}];
                }
              });
              pi.registerCommand('refresh',{handler:async(arg,ctx)=>{
                const result=await ctx.modelRegistry.refresh({allowNetwork:true,force:arg!=='fail',providers:['refresh']});
                if(!(result.errors instanceof Map))throw Error('missing error map');
                if(arg==='fail'){
                  if(!result.errors.get('refresh')?.message.includes('expected refresh failure'))throw Error('missing provider error');
                }else if(result.errors.size || !ctx.modelRegistry.find('refresh','online'))throw Error('refresh failed');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "refresh" });
        Assert.Equal("offline", session.Runner.Model.Id);
        var modelsPath = Path.Combine(fixture.AgentDirectory, "models.json");
        File.WriteAllText(modelsPath, File.ReadAllText(modelsPath).Replace("test-model", "changed-model", StringComparison.Ordinal));
        await DrainAsync(session.RunAsync("/refresh"));
        Assert.Equal("online", Assert.Single(session.Runner.GetModels("refresh")).Id);
        Assert.Contains(session.Runner.GetModels("session-provider"), model => model.Id == "changed-model");
        Assert.DoesNotContain(session.Runner.GetModels("session-provider"), model => model.Id == "test-model");
        await DrainAsync(session.RunAsync("/refresh fail"));
        Assert.Equal("online", Assert.Single(session.Runner.GetModels("refresh")).Id);
    }

    /// <summary>【CodingAgent】【动态缓存】原生提供方通过 publish 保存目录，新会话在选择模型之前从缓存恢复。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_CanonicalRefreshPublishesAndRestoresPersistentModels()
    {
        using var fixture = new Fixture("provider-cache", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cache.js"), """
            export default pi=>{
              let models=[];
              const model=id=>({id,name:id,provider:'cached',api:'openai-completions',baseUrl:'https://cache.invalid',input:['text'],cost:{input:1,output:2}});
              pi.registerProvider({id:'cached',name:'Cached',auth:{apiKey:{resolve:async()=>({auth:{apiKey:'fixture'},source:'fixture'})}},getModels:()=>models,
                async refreshModels(ctx){
                  if(ctx.allowNetwork){
                    const next=[model('persisted')];
                    if(!await ctx.publish({persist:{models:next,etag:'version-1',checkedAt:42},update:()=>{models=next;}}))throw Error('publication rejected');
                  }else await ctx.publish({update:()=>{models=ctx.stored?.models ?? [model('initial')];}});
                }
              });
              pi.registerCommand('cache',{handler:async(_,ctx)=>{
                const result=await ctx.modelRegistry.refresh({allowNetwork:true,providers:['cached']});
                if(result.errors.size)throw [...result.errors.values()][0];
              }});
            };
            """);
        var options = new CodingAgentSdkCreateSessionOptions
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "cached" };
        await using (var first = await CodingAgentSdk.CreateSessionAsync(options))
        {
            Assert.Equal("initial", first.Runner.Model.Id);
            await DrainAsync(first.RunAsync("/cache"));
            Assert.Equal("persisted", Assert.Single(first.Runner.GetModels("cached")).Id);
        }
        await using var restored = await CodingAgentSdk.CreateSessionAsync(options);
        Assert.Equal("persisted", restored.Runner.Model.Id);
        using var cached = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.AgentDirectory, "models-store.json")));
        Assert.Contains("version-1", cached.RootElement.GetRawText());
    }
}
