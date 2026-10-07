// 作者：xxx
using System.Text.Json;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【中间目录发布】发布成功后即使刷新失败或取消，宿主和磁盘仍保留已经提交的新目录。</summary>
    /// <param name="cancel">是否在发布完成后取消，另一分支主动失败。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtensionProvider_CompletedPublicationSurvivesLaterRefreshFailureOrCancellation(bool cancel)
    {
        using var fixture = new Fixture("refresh-publication", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "publication.js"), """
            export default pi=>{
              const cancel=__CANCEL__;
              const model=id=>({id,name:id,provider:'publication',api:'openai-completions',baseUrl:'https://fixture.test',input:['text']});
              let models=[model('initial')],published;
              pi.registerProvider({id:'publication',name:'Publication',
                auth:{apiKey:{check:async()=>({type:'api_key',source:'fixture'}),resolve:async()=>({auth:{apiKey:'fixture'},source:'fixture'})}},
                getModels:()=>models,
                async refreshModels(ctx){
                  if(!ctx.allowNetwork)return;
                  const next=[model('published')];
                  const order=[];
                  const rejected=ctx.publish({update:()=>{order.push('failed');throw Error('first publication failed');}});
                  const accepted=ctx.publish({persist:{models:next,etag:'published'},update:()=>{
                    if(order.join(',')!=='failed')throw Error('publications executed out of order');
                    models=next;
                  }});
                  const publications=await Promise.allSettled([rejected,accepted]);
                  if(publications[0].status!=='rejected'||publications[1].status!=='fulfilled'||publications[1].value!==true)throw Error('publication rejected');
                  published();
                  if(cancel){
                    await new Promise(resolve=>{if(ctx.signal.aborted)resolve();else ctx.signal.addEventListener('abort',resolve,{once:true});});
                    if(await ctx.publish({update:()=>{models=[model('cancelled')];}}))throw Error('cancelled update accepted');
                  }else throw Error('failure after publication');
                }
              });
              pi.registerCommand('publish-fail',{handler:async(_,ctx)=>{
                const controller=new AbortController();
                const ready=new Promise(resolve=>{published=resolve;});
                const pending=ctx.modelRegistry.refresh({providers:['publication'],allowNetwork:true,signal:controller.signal});
                await ready;if(cancel)controller.abort();
                const result=await pending;
                if(cancel?!result.aborted:!result.errors.get('publication')?.message.includes('failure after publication'))throw Error('wrong refresh result');
                if(!ctx.modelRegistry.find('publication','published')||ctx.modelRegistry.find('publication','initial'))throw Error('published catalog lost');
              }});
            };
            """.Replace("__CANCEL__", cancel.ToString().ToLowerInvariant()));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true,
            ProviderId = "publication", ModelId = "initial"
        }, deadline.Token);
        await DrainAsync(session.RunAsync("/publish-fail", deadline.Token));
        Assert.Equal("published", Assert.Single(session.Runner.GetModels("publication")).Id);
        using var stored = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.AgentDirectory, "models-store.json")));
        Assert.Contains("\"etag\":\"published\"", stored.RootElement.GetRawText().Replace(" ", "").Replace("\r", "").Replace("\n", ""));
    }
}
