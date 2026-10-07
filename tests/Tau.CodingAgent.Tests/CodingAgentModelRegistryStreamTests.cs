// 作者：xxx
using System.Net;
using System.Net.Sockets;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【目录流式】真实 HTTP 请求验证上下文、工具、重入请求钩子、流帧和独立会话边界。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_ModelRegistryStreamUsesRealTransportAndReentrantHooks()
    {
        using var fixture = new Fixture("registry-stream", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var endpoint = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stream.js"), $$$$$$"""
            import {AssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>{
              pi.registerProvider('registry-stream',{api:'openai-completions',baseUrl:'{{{{{{endpoint}}}}}}',apiKey:'registry-key',models:[{id:'chat'}]});
              pi.registerCommand('stream-registry',{handler:async(_,ctx)=>{
                const model=ctx.modelRegistry.find('registry-stream','chat');
                let payloadSeen=false,responseSeen=false,rawSeen=false,headerSeen=false;
                const stream=ctx.modelRegistry.streamSimple(model,{
                  systemPrompt:'A separate system prompt',messages:[{role:'user',content:'separate question',timestamp:Date.now()}],
                  tools:[{name:'test_tool',description:'test',parameters:{type:'object',properties:{value:{type:'string'}}}}]
                },{
                  maxTokens:17,reasoning:'off',
                  onPayload:async(payload,actual)=>{
                    if(actual.id!=='chat'||payload.model!=='chat'||await ctx.modelRegistry.getApiKey(model)!=='registry-key')throw Error('payload reentry failed');
                    payloadSeen=true;payload.temperature=0.45;return payload;
                  },
                  onResponse:response=>{if(response.status!==200)throw Error('response status');responseSeen=true;},
                  onProviderStreamEvent:event=>{if(event.choices)rawSeen=true;},
                  transformHeaders:headers=>{headerSeen=true;return {...headers,'X-Registry':'callback'};}
                });
                if(!(stream instanceof AssistantMessageEventStream))throw Error('wrong stream class');
                const types=[];for await(const event of stream)types.push(event.type);
                const result=await stream.result();
                if(result.stopReason!=='stop'||result.content[0].text!=='answer'||!types.includes('text_delta')||types.at(-1)!=='done')throw Error('stream/result mismatch '+JSON.stringify(result));
                if(!payloadSeen||!responseSeen||!rawSeen||!headerSeen)throw Error('missing request callback');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "registry-stream" }, deadline.Token);
        var served = ServeChatAsync(server, deadline.Token, body =>
        {
            Assert.Equal(0.45, body.GetProperty("temperature").GetDouble());
            Assert.Equal(17, body.GetProperty("max_completion_tokens").GetInt32());
            Assert.Contains("A separate system prompt", body.GetProperty("messages").GetRawText());
            Assert.Contains("separate question", body.GetProperty("messages").GetRawText());
            Assert.Equal("test_tool", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        });
        await DrainAsync(session.RunAsync("/stream-registry", deadline.Token));
        var headers = await served;
        Assert.Contains("Authorization: Bearer registry-key", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Registry: callback", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(session.Messages, message => message is UserMessage or AssistantMessage or ToolResultMessage);
    }

    /// <summary>【CodingAgent】【目录流式】原生 stream 与 streamSimple 分别调用，未知参数、工具参数及终值类型完整往返。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_ModelRegistryCompleteUsesNativeStreamAndPreservesCustomOptions()
    {
        using var fixture = new Fixture("registry-complete", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "complete.js"), """
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>{
              let full=0,simple=0;
              const stream=(model,context,options,kind)=>{
                if(options.customValue?.nested!==42||options.apiKey!=='native-key')throw Error('custom options/auth lost');
                const output=createAssistantMessageEventStream();
                const message={role:'assistant',api:model.api,provider:model.provider,model:model.id,timestamp:Date.now(),stopReason:'toolUse',
                  content:[{type:'toolCall',id:'call',name:'sample',arguments:{kind}}],
                  usage:{input:3,output:4,cacheRead:0,cacheWrite:0,totalTokens:7,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}};
                output.push({type:'start',partial:message});
                output.push({type:'toolcall_end',contentIndex:0,toolCall:message.content[0],partial:message});
                output.push({type:'done',reason:'toolUse',message});return output;
              };
              pi.registerProvider({id:'native-stream',name:'Native Stream',getModels:()=>[{id:'chat',provider:'native-stream',api:'native-custom-api',baseUrl:'http://127.0.0.1:1'}],
                auth:{apiKey:{name:'Native',resolve:async()=>({auth:{apiKey:'native-key'}})}},
                stream:(...args)=>{full++;return stream(...args,'full');},streamSimple:(...args)=>{simple++;return stream(...args,'simple');}});
              pi.registerCommand('complete-registry',{handler:async(_,ctx)=>{
                const model=ctx.modelRegistry.find('native-stream','chat'),context={messages:[]},options={customValue:{nested:42}};
                const result=await ctx.modelRegistry.complete(model,context,options);
                if(result.stopReason!=='toolUse'||result.content[0].arguments.kind!=='full'||result.usage.totalTokens!==7||full!==1||simple!==0)throw Error('full stream routing/result '+JSON.stringify(result));
                const output=ctx.modelRegistry.streamSimple(model,context,options);
                let tool;
                for await(const event of output)if(event.type==='toolcall_end')tool=event.toolCall;
                if(tool.arguments.kind!=='simple'||(await output.result()).stopReason!=='toolUse'||simple!==1)throw Error('simple stream routing');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "native-stream" });
        await DrainAsync(session.RunAsync("/complete-registry"));
        Assert.DoesNotContain(session.Messages, message => message is UserMessage or AssistantMessage or ToolResultMessage);
    }

    /// <summary>【CodingAgent】【目录取消】单次流取消产生 aborted 终值及部分文本，不影响父命令随后调用。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_ModelRegistryCancellationKeepsPartialAndParentUsable()
    {
        using var fixture = new Fixture("registry-stream-cancel", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cancel.js"), """
            import {createAssistantMessageEventStream} from '@earendil-works/pi-ai';
            export default pi=>{
              let calls=0;
              pi.registerProvider('registry-cancel',{api:'custom-api',apiKey:'test',baseUrl:'http://127.0.0.1:1',models:[{id:'chat'}],
                streamSimple(model,context,options){
                  const output=createAssistantMessageEventStream();
                  const message={role:'assistant',api:model.api,provider:model.provider,model:model.id,timestamp:Date.now(),stopReason:'stop',
                    content:[{type:'text',text:++calls===1?'partial':'reused'}],
                    usage:{input:0,output:0,cacheRead:0,cacheWrite:0,totalTokens:0,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}};
                  output.push({type:'text_delta',contentIndex:0,delta:message.content[0].text,partial:message});
                  if(calls>1)output.push({type:'done',reason:'stop',message});
                  else options.signal.addEventListener('abort',()=>output.push({type:'error',reason:'aborted',error:{...message,stopReason:'aborted'}}),{once:true});
                  return output;
                }});
              pi.registerCommand('cancel-registry',{handler:async(_,ctx)=>{
                const model=ctx.modelRegistry.find('registry-cancel','chat'),controller=new AbortController();
                const output=ctx.modelRegistry.streamSimple(model,{messages:[]},{signal:controller.signal});
                for await(const event of output)if(event.type==='text_delta')controller.abort();
                const result=await output.result();
                if(result.stopReason!=='aborted'||result.content[0].text!=='partial')throw Error('cancel result '+JSON.stringify(result));
                const next=await ctx.modelRegistry.complete(model,{messages:[]});
                if(next.stopReason!=='stop'||next.content[0].text!=='reused')throw Error('parent no longer usable');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "registry-cancel" });
        await DrainAsync(session.RunAsync("/cancel-registry"));
        Assert.DoesNotContain(session.Messages, message => message is UserMessage or AssistantMessage or ToolResultMessage);
    }
}
