// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【脚本能力端到端】验证目录脱敏、上下文校验、可信地址解析、并发额度、图像输出及用量计入。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CodeModeModelsUseSessionAuthLimitConcurrencyAndKeepUsage()
    {
        using var temp = TempDirectory.Create(); var agent = WriteCodeModeModelProvider(temp.Path);
        var provider = new CodeModeSessionProvider("""
            for(const type of ['image','classifier']){
              for(const method of ['getModelsOfType','getAvailableOfType']){
                const all=await models[method](type,'script-models');
                if(all.length!==1||all[0].headers!==undefined)throw Error('catalog leaked or lost model');
              }
            }
            if(await models.getModelOfType('image','script-models','missing')!==undefined)throw Error('missing model');
            const model=await models.getModelOfType('classifier','script-models','classify');
            if(model.headers!==undefined)throw Error('model headers leaked');
            model.baseUrl='http://untrusted.invalid';model.headers={Authorization:'wrong'};model.apiKey='wrong';
            const context={state:{text:'private prompt'},questions:{safe:{type:'bool',instructions:'classify',criteria:{true:'yes',false:'no'}}}};
            for(const bad of [null,{}, {state:[],questions:{}}, {state:{},questions:{}}, {state:{},questions:{q:{type:'bool',instructions:'x',criteria:{true:1,false:'no'}}}}]){
              const error=await models.classify(model,bad).then(()=>null,e=>e.message);
              if(!error?.includes('Expected context:'))throw Error('invalid context accepted');
            }
            const wrong=await models.generateImages(model,{input:[{type:'text',text:'draw'}]}).then(()=>null,e=>e.message);
            if(!wrong?.includes('not an image model'))throw Error('wrong model type accepted');
            const results=await Promise.all(Array.from({length:8},()=>models.classify(model,context)));
            if(results.some(result=>result.stopReason!=='stop'||result.answers.safe.probability!==0.75))throw Error('classification result');
            const imageModel=await models.getModelOfType('image','script-models','draw');
            const badImage=await models.generateImages(imageModel,{prompt:'wrong'}).then(()=>null,e=>e.message);
            if(!badImage?.includes('context.input'))throw Error('invalid images context');
            const result=await models.generateImages({...imageModel,baseUrl:'http://untrusted.invalid'}, {input:[{type:'text',text:'private image prompt'}]});
            if(result.responseId!=='image-result'||result.usage.totalTokens!==5)throw Error('image metadata');
            for(const block of result.output)if(block.type==='image')image(block);
            text('models completed');
            """,
            "const m=await models.getModelOfType('image','script-models','draw');await models.generateImages(m,{input:[{type:'text',text:'not displayed'}]});");
        var registry = new ProviderRegistry(); registry.Register("sdk-prompt-capture", () => provider, "test");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = registry,
            ProviderId = "test-provider", ModelId = "test-model", EnableCodeMode = true, Tools = ["codemode"],
            IncludeSkills = false, IncludePromptTemplates = false, IncludeContextFiles = false
        }, timeout.Token);
        await foreach (var ignored in session.RunAsync("models", timeout.Token)) { }
        var result = Assert.Single(session.Messages.OfType<ToolResultMessage>());
        Assert.False(result.IsError, string.Concat(result.Content.OfType<TextContent>().Select(block => block.Text)));
        Assert.Single(result.Content.OfType<ImageContent>()); Assert.Equal(45, result.Usage?.TotalTokens); Assert.Equal(27, result.Usage?.InputTokens);
        Assert.Equal(0.27m, result.Usage?.Cost?.Total);
        var details = Assert.IsType<JsonElement>(result.Details); var calls = details.GetProperty("calls").EnumerateArray().ToArray();
        Assert.Equal(9, calls.Length); Assert.All(calls, call => Assert.Equal("ok", call.GetProperty("status").GetString()));
        Assert.DoesNotContain("private prompt", details.GetRawText()); Assert.DoesNotContain("private image prompt", details.GetRawText());
        Assert.Equal("code-1/models.classify/1", calls[0].GetProperty("id").GetString());
        await foreach (var ignored in session.RunAsync("/code-model-check", timeout.Token)) { }
        await foreach (var ignored in session.RunAsync("undisplayed", timeout.Token)) { }
        Assert.Contains("returned 1 image that the script did not show", string.Concat(session.Messages.OfType<ToolResultMessage>().Last().Content.OfType<TextContent>().Select(block => block.Text)));
    }

    /// <summary>【CodingAgent】【脚本能力取消】超时取消活跃及排队请求，保留部分输出，下一个脚本仍可复用提供方。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CodeModeModelTimeoutCancelsQueueAndAllowsNextScript()
    {
        using var temp = TempDirectory.Create(); var agent = WriteCodeModeModelProvider(temp.Path);
        // 1. 【CodingAgent】【超时测试预算】为全量回归时的 JIT 和宿主调度留出时间，仍验证十二个已创建调用全部被超时取消
        var provider = new CodeModeSessionProvider("""
            // @options: {"timeout_ms":3000}
            const model=await models.getModelOfType('classifier','script-models','classify');
            text('before cancellation');
            await Promise.all(Array.from({length:12},()=>models.classify(model,{state:{cancel:true},questions:{safe:{type:'bool',instructions:'test',criteria:{true:'yes',false:'no'}}}})));
            """,
            "const m=await models.getModelOfType('classifier','script-models','classify');const r=await models.classify(m,{state:{error:true},questions:{safe:{type:'bool',instructions:'test',criteria:{true:'yes',false:'no'}}}});if(r.stopReason!=='error'||r.errorMessage!=='provider failed')throw Error('error result lost');text('recovered');");
        var registry = new ProviderRegistry(); registry.Register("sdk-prompt-capture", () => provider, "test");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = registry,
            ProviderId = "test-provider", ModelId = "test-model", EnableCodeMode = true, Tools = ["codemode"],
            IncludeSkills = false, IncludePromptTemplates = false, IncludeContextFiles = false
        }, timeout.Token);
        await foreach (var ignored in session.RunAsync("cancel", timeout.Token)) { }
        var failed = Assert.Single(session.Messages.OfType<ToolResultMessage>()); Assert.True(failed.IsError);
        var text = string.Concat(failed.Content.OfType<TextContent>().Select(block => block.Text)); Assert.Contains("before cancellation", text); Assert.Contains("Script timed out", text);
        var calls = Assert.IsType<JsonElement>(failed.Details).GetProperty("calls").EnumerateArray().ToArray(); Assert.Equal(12, calls.Length);
        Assert.All(calls, call => Assert.Equal("cancelled", call.GetProperty("status").GetString()));
        await foreach (var ignored in session.RunAsync("next", timeout.Token)) { }
        var last = session.Messages.OfType<ToolResultMessage>().Last(); Assert.False(last.IsError, string.Concat(last.Content.OfType<TextContent>().Select(block => block.Text)));
        Assert.Equal("error", Assert.IsType<JsonElement>(last.Details).GetProperty("calls")[0].GetProperty("status").GetString());
        Assert.Equal(5, last.Usage?.TotalTokens);
    }

    /// <summary>【CodingAgent】【脚本模拟提供方】创建无网络的分类和图像实现，检查最终认证和并发上限。</summary>
    /// <param name="directory">隔离工作目录。</param><returns>用户级代理目录。</returns>
    private static string WriteCodeModeModelProvider(string directory)
    {
        var agent = Path.Combine(directory, "agent"); var extensions = Path.Combine(agent, "extensions"); Directory.CreateDirectory(extensions);
        File.WriteAllText(Path.Combine(extensions, "models.js"), """
            // 作者：xxx
            export default pi=>{
              let active=0,maximum=0,started=0;
              const all=[{id:'classify',type:'classifier',provider:'script-models',api:'test-classifier',baseUrl:'http://catalog.test',headers:{'X-Credential':'private'}},
                {id:'draw',type:'image',provider:'script-models',api:'test-image',baseUrl:'http://catalog.test',headers:{'X-Credential':'private'},output:['image']}];
              const usage={input:3,output:2,cacheRead:0,cacheWrite:0,totalTokens:5,cost:{input:0.01,output:0.02,cacheRead:0,cacheWrite:0,total:0.03}};
              /** 【CodingAgent】【测试认证】@param {object} model 实际模型 @param {object} options 请求选项 @returns {void} 验证认证 */
              const check=(model,options)=>{
                if(model.baseUrl!=='http://resolved.test'||options.apiKey!=='fixture-key'||options.headers['X-Auth']!=='fixture'||options.env.TENANT!=='fixture')throw Error('auth or model identity mismatch');
              };
              pi.registerProvider({id:'script-models',getModels:()=>[],getAllModels:()=>all,
                auth:{apiKey:{name:'fixture',check:async()=>({type:'api_key',source:'fixture'}),resolve:async()=>({auth:{apiKey:'fixture-key',headers:{'X-Auth':'fixture'},baseUrl:'http://resolved.test'},env:{TENANT:'fixture'}})}},
                classify:async(model,context,options)=>{
                  check(model,options);active++;started++;maximum=Math.max(maximum,active);
                  try{
                    if(context.state.cancel)await new Promise((resolve,reject)=>{if(options.signal.aborted)reject(Error('cancelled'));else options.signal.addEventListener('abort',()=>reject(Error('cancelled')),{once:true});});
                    else await new Promise(resolve=>setTimeout(resolve,40));
                    return {api:model.api,provider:model.provider,model:model.id,answers:{safe:{type:'bool',probability:0.75}},usage,timestamp:123456789,
                      stopReason:context.state.error?'error':'stop',...(context.state.error?{errorMessage:'provider failed'}:{})};
                  }finally{active--;}
                },
                generateImages:async(model,context,options)=>{check(model,options);return {api:model.api,provider:model.provider,model:model.id,output:[{type:'image',mimeType:'image/png',data:'iVBORw0KGgo='}],usage,timestamp:123456789,stopReason:'stop',responseId:'image-result'};}});
              pi.registerCommand('code-model-check',{handler:()=>{if(active!==0||maximum!==4||started!==8)throw Error('concurrency or validation mismatch '+JSON.stringify({active,maximum,started}));}});
            };
            """);
        return agent;
    }
}
