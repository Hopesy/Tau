// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【提供方并行】一个提供方等待另一个提供方时，不得被顺序刷新阻塞。</summary><returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_RefreshesIndependentProvidersConcurrently()
    {
        using var fixture = new Fixture("refresh-parallel", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "parallel.js"), """
            export default pi=>{
              let release,started;
              pi.registerProvider('slow',{api:'openai-completions',baseUrl:'https://fixture.test',apiKey:'fixture',models:[{id:'initial'}],
                refreshModels:async ctx=>{
                  if(!ctx.allowNetwork)return;
                  started();
                  const concurrent=await new Promise(resolve=>{
                    const timer=setTimeout(()=>resolve(false),5000);
                    release=()=>{clearTimeout(timer);resolve(true);};
                  });
                  if(!concurrent)throw Error('providers refreshed sequentially');
                  return [{id:'slow-ready'}];
                }});
              pi.registerProvider('fast',{api:'openai-completions',baseUrl:'https://fixture.test',apiKey:'fixture',models:[{id:'initial'}],
                refreshModels:async ctx=>{if(ctx.allowNetwork){release();return [{id:'fast-ready'}];}}});
              pi.registerCommand('parallel-check',{handler:async(_,ctx)=>{
                const ready=new Promise(resolve=>{started=resolve;});
                const pending=ctx.modelRegistry.refresh({providers:['slow','fast'],allowNetwork:true});
                await ready;
                const result=await pending;
                if(result.errors.size)throw [...result.errors.values()][0];
                if(!ctx.modelRegistry.find('slow','slow-ready')||!ctx.modelRegistry.find('fast','fast-ready'))throw Error('parallel result missing');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "slow", ModelId = "initial" }, deadline.Token);
        await DrainAsync(session.RunAsync("/parallel-check", deadline.Token));
    }

    /// <summary>【CodingAgent】【替代刷新取消】新刷新或注销主动取消旧回调，旧操作不发布目录，也不污染当前错误结果。</summary>
    /// <param name="remove">是否注销提供方，另一分支启动新刷新。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtensionProvider_SupersededRefreshReceivesCancellationWithoutReportingFailure(bool remove)
    {
        using var fixture = new Fixture("refresh-superseded", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "superseded.js"), """
            export default pi=>{
              let started,cancelled;
              pi.registerProvider('superseded',{api:'openai-completions',baseUrl:'https://fixture.test',apiKey:'fixture',models:[{id:'initial'}],
                refreshModels:async ctx=>{
                  if(!ctx.allowNetwork)return;
                  if(ctx.force)return [{id:'newest'}];
                  started();
                  const aborted=await new Promise(resolve=>{
                    const timer=setTimeout(()=>resolve(false),5000);
                    const abort=()=>{clearTimeout(timer);resolve(true);};
                    if(ctx.signal.aborted)abort();else ctx.signal.addEventListener('abort',abort,{once:true});
                  });
                  if(!aborted){cancelled(false);throw Error('old refresh not cancelled');}
                  if(await ctx.publish({persist:{models:[]}}))throw Error('superseded publication accepted');
                  cancelled(true);throw Error('old callback failure after cancellation');
                }});
              pi.registerCommand('superseded-check',{handler:async(_,ctx)=>{
                const ready=new Promise(resolve=>{started=resolve;}),observed=new Promise(resolve=>{cancelled=resolve;});
                const first=ctx.modelRegistry.refresh({providers:['superseded'],allowNetwork:true});
                await ready;
                if(__REMOVE__)pi.unregisterProvider('superseded');
                else {
                  const latest=await ctx.modelRegistry.refresh({providers:['superseded'],allowNetwork:true,force:true});
                  if(latest.errors.size||latest.aborted)throw Error('new refresh failed');
                }
                const previous=await first;
                if(!await observed)throw Error('old refresh did not observe abort');
                if(previous.errors.size||previous.aborted)throw Error('superseded refresh poisoned caller result');
              }});
            };
            """.Replace("__REMOVE__", remove.ToString().ToLowerInvariant()));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "superseded", ModelId = "initial" }, deadline.Token);
        await DrainAsync(session.RunAsync("/superseded-check", deadline.Token));
        if (!remove) Assert.Equal("newest", Assert.Single(session.Runner.GetModels("superseded")).Id);
        Assert.False(File.Exists(Path.Combine(fixture.AgentDirectory, "models-store.json")));
    }
}
