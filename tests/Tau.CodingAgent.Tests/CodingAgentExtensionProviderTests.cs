// 作者：xxx
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;
using Tau.AgentCore;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【扩展流回归】原生流依次交付增量、工具调用和最终用量，宿主请求钩子可重入同一扩展。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_CustomStreamExecutesToolAndHostCallbacks()
    {
        using var fixture = new Fixture("custom-stream", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stream.js"), """
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>{
              let responses=0,rawEvents=0,toolEndNamespace;
              pi.on('message_update',event=>{
                const update=event.assistantMessageEvent;
                if(update?.type==='toolcall_end')toolEndNamespace=update.toolCall?.namespace;
              });
              pi.on('before_provider_request',event=>({...event.payload,changed:true}));
              pi.on('after_provider_response',()=>{responses++;});
              pi.on('provider_stream_event',()=>{rawEvents++;});
              pi.registerTool({name:'echo',description:'Echo',parameters:{type:'object',properties:{value:{type:'string'}},required:['value']},
                execute:async(_,args)=>({content:[{type:'text',text:args.value}]})});
              pi.registerProvider('custom-stream',{api:'custom-chat',baseUrl:'https://unused.invalid',apiKey:'test-stream-key',
                models:[{id:'stream',name:'Stream',contextWindow:8192,maxTokens:64}],
                streamSimple(model,context,options){
                  const stream=createAssistantMessageEventStream();
                  const message={role:'assistant',api:model.api,provider:model.provider,model:model.id,content:[],stopReason:'stop',
                    usage:{input:11,output:7,cacheRead:3,cacheWrite:2,cost:{input:0.01,output:0.02,total:0.03}},timestamp:Date.now()};
                  (async()=>{
                    if(options.apiKey!=='test-stream-key')throw Error('missing resolved key');
                    if(!context.messages.some(m=>m.role==='system'))throw Error('missing transcript');
                    const payload=await options.onPayload({initial:true},model);
                    if(!payload.changed)throw Error('host payload callback lost');
                    await options.onResponse({status:200,headers:{'x-test':'ok'}},model);
                    await options.onProviderStreamEvent({event:'native'},model);
                    stream.push({type:'start',partial:message});
                    if(!context.messages.some(m=>m.role==='toolResult')){
                      message.content=[{type:'toolCall',id:'extension-tool',name:'echo',arguments:{value:'tool result'},namespace:'extension-tools'}];
                      message.stopReason='toolUse';
                      stream.push({type:'toolcall_start',contentIndex:0,partial:message});
                      stream.push({type:'toolcall_delta',contentIndex:0,delta:'{"value":"tool result"}',partial:message});
                      stream.push({type:'toolcall_end',contentIndex:0,toolCall:message.content[0],partial:message});
                    }else{
                      if(responses!==2||rawEvents!==2)throw Error('host observations lost');
                      const previous=context.messages.find(m=>m.role==='assistant'&&m.content.some(c=>c.type==='toolCall'));
                      if(previous?.content[0].namespace!=='extension-tools')throw Error('tool namespace lost');
                      if(toolEndNamespace!=='extension-tools')throw Error('tool end namespace lost');
                      message.content=[{type:'text',text:''}];stream.push({type:'text_start',contentIndex:0,partial:message});
                      for(const delta of ['流式','完成']){
                        await new Promise(resolve=>setTimeout(resolve,20));message.content[0].text+=delta;
                        stream.push({type:'text_delta',contentIndex:0,delta,partial:message});
                      }
                      stream.push({type:'text_end',contentIndex:0,content:message.content[0].text,partial:message});
                    }
                    stream.push({type:'done',reason:message.stopReason,message});
                  })().catch(error=>{message.stopReason='error';message.errorMessage=error.message;stream.push({type:'error',reason:'error',error:message});});
                  return stream;
                }
              });
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "custom-stream", Tools = ["echo"] });
        var deltas = new List<string>();
        await foreach (var item in session.RunAsync("run"))
        {
            if (item is AgentEndEvent end) Assert.Null(end.ErrorMessage);
            if (item is MessageUpdateEvent { StreamEvent: TextDeltaEvent delta }) deltas.Add(delta.Delta);
        }
        Assert.Equal(["流式", "完成"], deltas);
        var assistants = session.Messages.OfType<AssistantMessage>().ToArray();
        Assert.Equal(2, assistants.Length);
        Assert.Equal(StopReason.ToolUse, assistants[0].StopReason);
        Assert.Equal("extension-tools", Assert.Single(assistants[0].Content.OfType<ToolCallContent>()).Namespace);
        Assert.Equal("流式完成", Assert.Single(assistants[1].Content.OfType<TextContent>()).Text);
        Assert.Equal(11, assistants[1].Usage?.InputTokens);
        Assert.Equal(3, assistants[1].Usage?.CacheReadTokens);
        Assert.Equal("tool result", Assert.Single(session.Messages.OfType<ToolResultMessage>()).Content.OfType<TextContent>().Single().Text);
    }

    /// <summary>【CodingAgent】【扩展流取消】取消传递给扩展的 AbortSignal，合作结束后同一进程及工厂状态仍可复用。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_CancellationPreservesWorkerAndFactoryState()
    {
        using var fixture = new Fixture("stream-cancel", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stream.js"), """
            import {AssistantMessageEventStream} from '@earendil-works/pi-ai/utils/event-stream';
            export default pi=>{
              let calls=0,cancelled=false;
              pi.registerProvider('cancel-stream',{api:'custom-chat',baseUrl:'https://unused.invalid',apiKey:'test',models:[{id:'stream'}],
                streamSimple(model,context,options){
                  const stream=new AssistantMessageEventStream();
                  const message={role:'assistant',content:[{type:'text',text:'started'}],stopReason:'stop',timestamp:Date.now()};
                  calls++;
                  stream.push({type:'text_delta',contentIndex:0,delta:'started',partial:message});
                  if(calls===1){
                    const abort=()=>{cancelled=true;message.stopReason='aborted';message.errorMessage='cancelled';stream.push({type:'error',reason:'aborted',error:message});};
                    if(options.signal.aborted)abort();else options.signal.addEventListener('abort',abort,{once:true});
                  }else{
                    message.content[0].text=cancelled?'reused':'lost-state';stream.push({type:'done',reason:'stop',message});
                  }
                  return stream;
                }
              });
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "cancel-stream", NoTools = CodingAgentSdkNoToolsMode.All });
        await foreach (var item in session.RunAsync("first"))
            if (item is MessageUpdateEvent { StreamEvent: TextDeltaEvent }) session.Runner.Abort();
        Assert.Equal(StopReason.Aborted, session.Messages.OfType<AssistantMessage>().Last().StopReason);
        await DrainAsync(session.RunAsync("second"));
        Assert.Equal("reused", session.Messages.OfType<AssistantMessage>().Last().Content.OfType<TextContent>().Single().Text);
    }

    /// <summary>【CodingAgent】【扩展提供方】扩展模型可作为启动模型，真实 HTTP 请求携带扩展和模型头。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_FactoryRegistrationRoutesActualRequest()
    {
        using var fixture = new Fixture("extension", "models");
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var served = ServeChatAsync(server, deadline.Token);
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "provider.js"), $$$"""
            export default pi=>pi.registerProvider('extension-provider',{
              api:'unused-custom',streamSimple(){throw Error('wrong protocol selected');},
              baseUrl:'http://127.0.0.1:{{{((IPEndPoint)server.LocalEndpoint).Port}}}',apiKey:'test-extension-key',
              headers:{'X-Extension':'registered'},models:[{id:'custom',api:'openai-completions',name:'Custom',input:['text'],reasoning:false,
              contextWindow:8192,maxTokens:64,headers:{'X-Model':'specific'},cost:{input:0,output:0,cacheRead:0,cacheWrite:0}}]
            });
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "extension-provider", NoTools = CodingAgentSdkNoToolsMode.All });
        Assert.Equal("custom", session.Runner.Model.Id);
        Assert.True(session.Runner.GetAuthStatus().IsConfigured);
        Assert.Equal("test-extension-key", session.Runner.ResolveModelApiKey(session.Runner.Model));
        await DrainAsync(session.RunAsync("test", deadline.Token));
        var request = await served;
        Assert.Contains("POST /chat/completions", request);
        Assert.Contains("Bearer test-extension-key", request);
        Assert.Contains("X-Extension: registered", request);
        Assert.Contains("X-Model: specific", request);
        Assert.Equal("answer", Assert.Single(session.Messages.OfType<AssistantMessage>()).Content.OfType<TextContent>().Single().Text);
        Assert.DoesNotContain("test-extension-key", File.ReadAllText(Path.Combine(fixture.AgentDirectory, "models.json")));
        Assert.DoesNotContain(session.ExtensionStatus.Diagnostics, diagnostic => diagnostic.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>【CodingAgent】【动态提供方】同一命令内注册后查询与选择可用，注销恢复 SDK 原目录且不污染其他会话。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_LiveRegistrationUnregisterAndReloadAreIsolated()
    {
        using var fixture = new Fixture("dynamic-provider", "models");
        var catalog = fixture.Catalog();
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "provider.js");
        File.WriteAllText(file, """
            export default pi=>{
              pi.registerCommand('add',{handler:async(_,ctx)=>{
                pi.registerProvider('session-provider',{baseUrl:'https://extension.invalid/v1',apiKey:'runtime-key',models:[{id:'runtime',name:'Runtime'}]});
                const model=ctx.modelRegistry.find('session-provider','runtime');
                if(!model || model.baseUrl!=='https://extension.invalid/v1')throw Error('stale directory');
                if(!(await pi.setModel(model)))throw Error('select failed');
                if(await ctx.modelRegistry.getApiKey(model)!=='runtime-key')throw Error('stale auth');
              }});
              pi.registerCommand('remove',{handler:async(_,ctx)=>{
                pi.unregisterProvider('session-provider');
                if(ctx.modelRegistry.find('session-provider','runtime'))throw Error('stale removed model');
                const model=ctx.modelRegistry.find('session-provider','test-model');
                if(!model || !(await pi.setModel(model)))throw Error('restore failed');
              }});
              pi.registerCommand('reload',{handler:async(_,ctx)=>{await ctx.reload();}});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model", ModelCatalog = catalog });
        await DrainAsync(session.RunAsync("/add"));
        Assert.Equal("runtime", session.Runner.Model.Id);
        Assert.Single(session.Runner.GetModels("session-provider"));
        Assert.Equal("runtime-key", session.Runner.ResolveModelApiKey(session.Runner.Model));
        Assert.Null(catalog.TryGetModel("session-provider", "runtime"));
        Assert.Equal(2, catalog.GetModels("session-provider").Count);
        await DrainAsync(session.RunAsync("/remove"));
        Assert.Equal("test-model", session.Runner.Model.Id);
        Assert.Equal(2, session.Runner.GetModels("session-provider").Count);
        Assert.Equal("models-dynamic-provider", session.Runner.ResolveModelApiKey(session.Runner.Model));
        await DrainAsync(session.RunAsync("/add"));
        await DrainAsync(session.RunAsync("/reload"));
        Assert.DoesNotContain(session.Runner.GetModels("session-provider"), model => model.Id == "runtime");
        Assert.Equal(2, catalog.GetModels("session-provider").Count);
    }

    /// <summary>【AI】【注册事务】无效替换保留旧状态，空模型数组移除全部聊天模型，注销恢复原模型。</summary>
    [Fact]
    public void ExtensionProvider_CatalogRejectsInvalidDefinitionsWithoutLosingPreviousRegistration()
    {
        var configuration = new ModelConfigurationStore([]);
        var catalog = new ModelCatalog(new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: configuration), configuration);
        var original = catalog.GetModels("openai");
        using var valid = JsonDocument.Parse("""{"api":"openai-responses","baseUrl":"https://example.invalid","apiKey":"test","models":[{"id":"new"}]}""");
        catalog.SetRuntimeProviders(new Dictionary<string, JsonElement> { ["openai"] = valid.RootElement });
        Assert.Equal("new", Assert.Single(catalog.GetModels("openai")).Id);
        using var invalid = JsonDocument.Parse("""{"models":[{"id":"broken"}]}""");
        Assert.Throws<ArgumentException>(() => catalog.SetRuntimeProviders(new Dictionary<string, JsonElement> { ["unknown"] = invalid.RootElement }));
        Assert.Equal("new", Assert.Single(catalog.GetModels("openai")).Id);
        using var empty = JsonDocument.Parse("""{"models":[]}""");
        catalog.SetRuntimeProviders(new Dictionary<string, JsonElement> { ["openai"] = empty.RootElement });
        Assert.Empty(catalog.GetModels("openai"));
        catalog.SetRuntimeProviders(new Dictionary<string, JsonElement>());
        Assert.Equal(original, catalog.GetModels("openai"));
    }
}
